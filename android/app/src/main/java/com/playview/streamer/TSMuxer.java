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
    private static final int NULL_PID = 0x1FFF;

    private final OutputStream out;
    // Continuity counters — one per PID domain: 0 video, 1 audio, 2 PAT, 3 PMT.
    private final int[] cc = new int[4];
    private boolean tablesSent;
    /** Wall clock of the last write that actually reached the client —
     *  the session watchdog keys off this to detect a dead client even when
     *  no frames flow (static screen). */
    private volatile long lastDataWriteMs = System.currentTimeMillis();

    TSMuxer(OutputStream out) {
        this.out = out;
    }

    /** Milliseconds since epoch of the last successful client write. */
    long lastDataWriteMs() {
        return lastDataWriteMs;
    }

    // ── Public API ──────────────────────────────────────────────────────────

    /** Sends PAT + PMT right away, so VLC sees the program before the first frame. */
    synchronized void writeTables() throws IOException {
        ensureTables();
        lastDataWriteMs = System.currentTimeMillis();
    }

    /** Writes one H.264 access unit (Annex-B). ptsUs is the presentation time in µs. */
    synchronized void writeVideo(byte[] data, int len, long ptsUs) throws IOException {
        ensureTables();
        writePes(VIDEO_PID, 0, 0xE0, data, len, ptsUs, true);
        lastDataWriteMs = System.currentTimeMillis();
    }

    /** Writes one AAC frame (raw, without ADTS — the header is added here). */
    synchronized void writeAudio(byte[] data, int len, long ptsUs) throws IOException {
        ensureTables();
        writePes(AUDIO_PID, 1, 0xC0, data, len, ptsUs, false);
        lastDataWriteMs = System.currentTimeMillis();
    }

    /**
     * Writes one null packet (PID 0x1FFF). Pure padding every TS player
     * ignores — used as a keepalive so a disconnected client is detected by
     * the failing write instead of hanging the session forever.
     */
    synchronized void writeNullPacket() throws IOException {
        byte[] pk = new byte[188];
        pk[0] = 0x47;
        pk[1] = (byte) (NULL_PID >> 8);   // 0x1F
        pk[2] = (byte) NULL_PID;          // 0xFF
        pk[3] = 0x10;                     // no PUSI, null packets carry no CC
        for (int i = 4; i < 188; i++) pk[i] = (byte) 0xFF;
        out.write(pk);
        lastDataWriteMs = System.currentTimeMillis();
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
        writeSection(t, s, p); // fills section_length + CRC at t[p..p+4)
        writeTablePacket(t, p + 4, 0x0000, 2); // PAT keeps its own continuity counter
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
        writeTablePacket(t, p + 4, PMT_PID, 3); // and the PMT its own
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

    /** Emits one TS packet whose payload is the section at t[4..payloadEnd) —
     *  the first 4 bytes of t are a placeholder for the TS header, not payload. */
    private void writeTablePacket(byte[] t, int payloadEnd, int pid, int ccIdx) throws IOException {
        byte[] pk = new byte[188];
        pk[0] = 0x47;
        pk[1] = (byte) (0x40 | ((pid >> 8) & 0x1F)); // PUSI set
        pk[2] = (byte) (pid & 0xFF);
        pk[3] = (byte) (0x10 | (cc[ccIdx] & 0x0F));
        cc[ccIdx] = (cc[ccIdx] + 1) & 0x0F;
        int payloadLen = payloadEnd - 4;
        int chunk = Math.min(184, payloadLen);
        System.arraycopy(t, 4, pk, 4, chunk);
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
        pes[p++] = (byte) (((pts >> 14) & 0xFE) | 0x01);
        pes[p++] = (byte) ((pts >> 7) & 0xFF);
        pes[p++] = (byte) (((pts << 1) & 0xFE) | 0x01);
        System.arraycopy(es, 0, pes, p, esLen);
        p += esLen;

        int pos = 0;
        boolean first = true;
        while (pos < p) {
            // A PCR adaptation field takes 12 bytes of header space
            // (4 TS header + 1 AF length + 1 flags + 6 PCR) → payload ≤ 176.
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

        int o = 4;
        if (withPcr) {
            // Correct PCR: exactly 6 bytes (33-bit base + 6 reserved + 9-bit ext).
            // AF = 1 flags byte + 6 PCR bytes → declared length 7, payload at o == 12.
            pk[3] = (byte) (0x30 | (cc[ccIdx] & 0x0F));
            pk[o++] = 7;   // adaptation_field_length
            pk[o++] = 0x10; // PCR flag
            long pcr = (pcrUs * 9 / 100) & 0x1FFFFFFFFL;
            pk[o++] = (byte) (pcr >>> 25);          // base[32:25]
            pk[o++] = (byte) (pcr >>> 17);          // base[24:17]
            pk[o++] = (byte) (pcr >>> 9);           // base[16:9]
            pk[o++] = (byte) (pcr >>> 1);           // base[8:1]
            pk[o++] = (byte) ((pcr & 1) << 7);      // base[0] + reserved + ext[8:7]=0
            pk[o++] = 0x00;                          // ext[6:0] << 1, ext flag 0
            // o == 12; caller guarantees chunk ≤ 176.
        } else if (chunk < 184) {
            // Stuffing-only adaptation field: 1 length + 1 flags + (len-1) × 0xFF.
            // A chunk of exactly 183 gives len == 0 — then only the length byte
            // exists (no flags!) and the payload starts right after it.
            pk[3] = (byte) (0x30 | (cc[ccIdx] & 0x0F));
            int len = 183 - chunk;
            pk[o++] = (byte) len;
            if (len > 0) {
                pk[o++] = 0x00; // flags: nothing optional
                for (int i = 0; i < len - 1; i++) pk[o++] = (byte) 0xFF;
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
