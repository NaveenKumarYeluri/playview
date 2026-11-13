package com.playview.streamer;

import java.io.IOException;
import java.io.OutputStream;

/**
 * Minimal MPEG-2 TS muxer for one H.264 (Annex-B) + one AAC (ADTS) elementary
 * stream — the standard IPTV container that VLC plays natively. No dependencies.
 * <p>
 * Emits PAT + PMT once, then PES packets split into 188-byte TS packets, with
 * PCR carried on the video PID. All methods are thread-safe (video and audio
 * pipelines write from separate threads).
 */
final class TSMuxer {

    private static final int VIDEO_PID = 0x0100;
    private static final int AUDIO_PID = 0x0101;
    private static final int PMT_PID = 0x1000;

    private final OutputStream out;
    private final int[] cc = new int[2]; // continuity counters: 0 = video, 1 = audio
    private boolean tablesSent;

    TSMuxer(OutputStream out) {
        this.out = out;
    }

    // ── Public API ──────────────────────────────────────────────────────────

    /** Writes one H.264 access unit (Annex-B). ptsUs is the presentation time in µs. */
    synchronized void writeVideo(byte[] data, int len, long ptsUs) throws IOException {
        ensureTables();
        writePes(VIDEO_PID, 0, 0xE0, data, len, ptsUs, true);
    }

    /** Writes one AAC frame (raw, without ADTS — the header is added here). */
    synchronized void writeAudio(byte[] data, int len, long ptsUs) throws IOException {
        ensureTables();
        writePes(AUDIO_PID, 1, 0xC0, data, len, ptsUs, false);
    }

    private void ensureTables() throws IOException {
        if (tablesSent) return;
        writePat();
        writePmt();
        tablesSent = true;
    }

    // ── PSI tables ──────────────────────────────────────────────────────────

    private void writePat() throws IOException {
        byte[] t = new byte[188];
        int p = 4;
        t[p++] = 0x00; // pointer_field
        int s = p;
        t[p++] = 0x00; // table_id (PAT)
        p += 2;        // section_length (filled below)
        t[p++] = 0x00; t[p++] = 0x01; // transport_stream_id
        t[p++] = (byte) 0xC1;         // reserved, version 0, current_next
        t[p++] = 0x00;                // section_number
        t[p++] = 0x00;                // last_section_number
        t[p++] = 0x00; t[p++] = 0x01; // program_number = 1
        t[p++] = (byte) (0xE0 | ((PMT_PID >> 8) & 0x1F));
        t[p++] = (byte) (PMT_PID & 0xFF);
        writeSection(t, s, p);
        writeTablePacket(t, p, 0x0000, 0);
    }

    private void writePmt() throws IOException {
        byte[] t = new byte[188];
        int p = 4;
        t[p++] = 0x00; // pointer_field
        int s = p;
        t[p++] = 0x02; // table_id (PMT)
        p += 2;        // section_length (filled below)
        t[p++] = 0x00; t[p++] = 0x01; // program_number
        t[p++] = (byte) 0xC1;
        t[p++] = 0x00; t[p++] = 0x00;
        t[p++] = (byte) (0xE0 | ((VIDEO_PID >> 8) & 0x1F)); // PCR PID = video
        t[p++] = (byte) (VIDEO_PID & 0xFF);
        t[p++] = (byte) 0xF0; t[p++] = 0x00; // program_info_length = 0

        t[p++] = 0x1B; // H.264
        t[p++] = (byte) (0xE0 | ((VIDEO_PID >> 8) & 0x1F));
        t[p++] = (byte) (VIDEO_PID & 0xFF);
        t[p++] = (byte) 0xF0; t[p++] = 0x00;

        t[p++] = 0x0F; // AAC (ADTS)
        t[p++] = (byte) (0xE0 | ((AUDIO_PID >> 8) & 0x1F));
        t[p++] = (byte) (AUDIO_PID & 0xFF);
        t[p++] = (byte) 0xF0; t[p++] = 0x00;

        writeSection(t, s, p);
        writeTablePacket(t, p, PMT_PID, 0);
    }

    /** Fills the section_length field and appends the CRC32 at position p. */
    private void writeSection(byte[] t, int sectionStart, int p) {
        int length = p + 4 - (sectionStart + 3);
        t[sectionStart + 1] = (byte) (0xB0 | ((length >> 8) & 0x0F));
        t[sectionStart + 2] = (byte) (length & 0xFF);
        int crc = crc32(t, sectionStart, p - sectionStart);
        t[p++] = (byte) (crc >>> 24);
        t[p++] = (byte) (crc >>> 16);
        t[p++] = (byte) (crc >>> 8);
        t[p++] = (byte) crc;
    }

    private void writeTablePacket(byte[] t, int payloadLen, int pid, int ccIdx) throws IOException {
        byte[] pk = new byte[188];
        pk[0] = 0x47;
        pk[1] = (byte) (0x40 | ((pid >> 8) & 0x1F)); // PUSI set
        pk[2] = (byte) (pid & 0xFF);
        pk[3] = (byte) (0x10 | (cc[ccIdx] & 0x0F));
        cc[ccIdx] = (cc[ccIdx] + 1) & 0x0F;
        int chunk = Math.min(184, payloadLen);
        System.arraycopy(t, 0, pk, 4, chunk);
        for (int i = 4 + chunk; i < 188; i++) pk[i] = (byte) 0xFF;
        out.write(pk);
    }

    // ── PES ─────────────────────────────────────────────────────────────────

    private void writePes(int pid, int ccIdx, int streamId, byte[] es, int esLen,
                          long ptsUs, boolean pcr) throws IOException {
        int headerLen = 14; // 00 00 01 sid len2 80 80 05 pts5
        byte[] pes = new byte[headerLen + esLen];
        int p = 0;
        pes[p++] = 0x00; pes[p++] = 0x00; pes[p++] = 0x01;
        pes[p++] = (byte) streamId;
        if (streamId == 0xE0) {
            // Video: unbounded PES length (6×264 rule for TS).
            pes[p++] = 0x00; pes[p++] = 0x00;
        } else {
            int pesLen = 3 + 5 + esLen; // flags(3) + PTS(5) + payload
            pes[p++] = (byte) (pesLen >> 8);
            pes[p++] = (byte) pesLen;
        }
        pes[p++] = (byte) 0x80; // marker '10', PTS only
        pes[p++] = (byte) 0x80; // PTS_DTS_flags = '10'
        pes[p++] = 0x05;        // header_data_length
        long pts = ptsUs * 9 / 100; // µs → 90 kHz
        pes[p++] = (byte) (0x21 | ((pts >> 29) & 0x0E));
        pes[p++] = (byte) ((pts >> 22) & 0xFF);
        pes[p++] = (byte) (0x01 | ((pts >> 14) & 0xFF));
        pes[p++] = (byte) ((pts >> 7) & 0xFF);
        pes[p++] = (byte) (0x01 | ((pts << 1) & 0xFF));
        System.arraycopy(es, 0, pes, p, esLen);
        p += esLen;

        int pos = 0;
        boolean first = true;
        while (pos < p) {
            int avail = (first && pcr) ? 176 : 184;
            int chunk = Math.min(avail, p - pos);
            emitPacket(pid, ccIdx, first, pes, pos, chunk, first && pcr, ptsUs);
            pos += chunk;
            first = false;
        }
    }

    // ── TS packetization ────────────────────────────────────────────────────

    private void emitPacket(int pid, int ccIdx, boolean pusi, byte[] pes, int pesPos,
                            int chunk, boolean withPcr, long pcrUs) throws IOException {
        byte[] pk = new byte[188];
        pk[0] = 0x47;
        pk[1] = (byte) ((pusi ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        pk[2] = (byte) (pid & 0xFF);

        boolean hasAf = withPcr || chunk < 184;
        int o = 4;
        if (hasAf) {
            int len = 183 - chunk; // bytes after the AF length byte
            pk[3] = (byte) (0x30 | (cc[ccIdx] & 0x0F));
            pk[o++] = (byte) len;
            if (withPcr) {
                pk[o++] = 0x10; // PCR flag
                long pcr = (pcrUs * 9 / 100) & 0x1FFFFFFFFL;
                pk[o++] = (byte) (pcr >>> 25);
                pk[o++] = (byte) (pcr >>> 17);
                pk[o++] = (byte) (pcr >>> 9);
                pk[o++] = (byte) (pcr >>> 1);
                pk[o++] = (byte) (((pcr & 1) << 7));
                pk[o++] = 0x00; // extension = 0
                pk[o++] = 0x00;
                for (; o < 4 + 1 + len; o++) pk[o] = (byte) 0xFF;
            } else {
                pk[o++] = 0x00; // no optional fields, pure stuffing
                for (; o < 4 + 1 + len; o++) pk[o] = (byte) 0xFF;
            }
        } else {
            pk[3] = (byte) (0x10 | (cc[ccIdx] & 0x0F));
        }
        cc[ccIdx] = (cc[ccIdx] + 1) & 0x0F;

        System.arraycopy(pes, pesPos, pk, o, chunk);
        out.write(pk);
    }

    // ── CRC32 (MPEG-2) ──────────────────────────────────────────────────────

    private static int crc32(byte[] data, int off, int len) {
        int crc = 0xFFFFFFFF;
        for (int i = off; i < off + len; i++) {
            crc ^= (data[i] & 0xFF) << 24;
            for (int j = 0; j < 8; j++) {
                crc = ((crc & 0x80000000) != 0)
                        ? (crc << 1) ^ 0x04C11DB7
                        : (crc << 1);
            }
        }
        return crc;
    }
}
