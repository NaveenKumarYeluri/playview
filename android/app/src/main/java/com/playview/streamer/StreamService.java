package com.playview.streamer;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.IBinder;
import android.util.DisplayMetrics;
import android.view.Surface;
import android.view.WindowManager;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.util.ArrayList;

/**
 * Captures the phone screen (video) and playback audio, hardware-encodes both,
 * and serves them as a standard MPEG-TS stream over TCP — which PlayView plays
 * with LibVLC like any other network stream. VLC connects with a plain HTTP
 * request; the phone answers with a minimal 200 and a raw TS body.
 * <p>
 * One client at a time; the stream restarts (fresh PAT/PMT + SPS/PPS) for every
 * new connection, so the laptop can always join mid-session.
 */
public class StreamService extends Service {

    public static final int PORT = 48335;
    private static final String CHANNEL_ID = "playview";

    private MediaProjection projection;
    private volatile boolean running;
    private Thread serverThread;
    private volatile Socket currentClient;
    private static volatile boolean active;

    public static boolean isActive() {
        return active;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    @SuppressWarnings("deprecation")
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && "stop".equals(intent.getAction())) {
            shutdown();
            return START_NOT_STICKY;
        }

        startForegroundWithNotice();

        if (intent == null || running) return START_STICKY;

        int resultCode = intent.getIntExtra("resultCode", -1);
        Intent data = intent.getParcelableExtra("data");
        if (data == null) {
            stopSelf();
            return START_NOT_STICKY;
        }

        MediaProjectionManager mpm =
                (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        MediaProjection p = mpm.getMediaProjection(resultCode, data);
        if (p == null) {
            stopSelf();
            return START_NOT_STICKY;
        }
        projection = p;
        p.registerCallback(new MediaProjection.Callback() {
            @Override
            public void onStop() {
                shutdown();
            }
        }, null);

        active = true;
        running = true;
        serverThread = new Thread(this::serveLoop, "playview-server");
        serverThread.start();
        return START_STICKY;
    }

    private void shutdown() {
        active = false;
        running = false;

        Socket c = currentClient;
        if (c != null) {
            try { c.close(); } catch (Exception ignored) { }
        }
        if (serverThread != null) serverThread.interrupt();

        if (projection != null) {
            try { projection.stop(); } catch (Exception ignored) { }
            projection = null;
        }

        stopForeground(true);
        stopSelf();
    }

    @Override
    public void onDestroy() {
        shutdown();
        super.onDestroy();
    }

    // ── Server loop ─────────────────────────────────────────────────────────

    private void serveLoop() {
        ServerSocket server = null;
        try {
            server = new ServerSocket();
            server.setReuseAddress(true);
            server.bind(new InetSocketAddress(PORT));
            while (running) {
                Socket client = server.accept();
                currentClient = client;
                try {
                    serveHttp(client);
                } catch (Throwable ignored) {
                    // Client gone or device hiccup — wait for the next connection.
                } finally {
                    try { client.close(); } catch (Exception ignored) { }
                    currentClient = null;
                    if (running) {
                        try { Thread.sleep(200); } catch (InterruptedException e) { break; }
                    }
                }
            }
        } catch (IOException ignored) {
            // Port unavailable — service idles; Stop clears it.
        } finally {
            if (server != null) {
                try { server.close(); } catch (Exception ignored) { }
            }
        }
    }

    // ── HTTP: read the request, answer, then stream the TS body ─────────────

    private void serveHttp(Socket client) throws Exception {
        client.setTcpNoDelay(true);

        // Read the request head (VLC sends a GET) — best effort.
        try {
            client.setSoTimeout(5000);
            InputStream in = client.getInputStream();
            ByteArrayOutputStream req = new ByteArrayOutputStream();
            byte[] buf = new byte[1024];
            int n;
            while ((n = in.read(buf)) > 0) {
                req.write(buf, 0, n);
                String head = req.toString("ISO-8859-1");
                if (head.contains("\r\n\r\n") || req.size() > 8192) break;
            }
        } catch (SocketTimeoutException ignored) {
            // No request sent — serve the body anyway (raw TCP clients).
        }

        OutputStream out = client.getOutputStream();
        out.write(("HTTP/1.1 200 OK\r\n"
                + "Content-Type: video/mp2t\r\n"
                + "Connection: close\r\n"
                + "Cache-Control: no-cache\r\n"
                + "\r\n").getBytes("ISO-8859-1"));
        out.flush();

        streamTo(client, out);
    }

    private void streamTo(Socket client, OutputStream out) throws Exception {
        DisplayMetrics m = new DisplayMetrics();
        WindowManager wm = (WindowManager) getSystemService(WINDOW_SERVICE);
        wm.getDefaultDisplay().getRealMetrics(m);

        // ~2/3 scale keeps USB / Wi-Fi bandwidth comfortable; even dimensions only.
        int w = m.widthPixels * 2 / 3;  w -= w % 2;
        int h = m.heightPixels * 2 / 3; h -= h % 2;

        TSMuxer mux = new TSMuxer(out);

        VideoPipeline video = new VideoPipeline(mux, w, h, m.densityDpi);
        AudioPipeline audio = Build.VERSION.SDK_INT >= 29
                ? new AudioPipeline(mux)
                : null;

        try {
            video.start();
            if (audio != null) audio.start();

            // Block until the video pipeline ends (client disconnect, stop…).
            while (video.drainThread.isAlive() && running) {
                video.drainThread.join(200);
            }
        } finally {
            if (audio != null) audio.stop();
            video.stop();
        }
    }

    // ── Video pipeline (MediaProjection → H.264 → TSMuxer) ──────────────────

    private final class VideoPipeline {
        final TSMuxer mux;
        final int w, h, dpi;
        MediaCodec encoder;
        VirtualDisplay display;
        Surface input;
        Thread drainThread;

        // SPS/PPS arrive as a codec-config buffer; they must precede the first
        // frame so VLC can start decoding mid-stream.
        byte[] codecConfig;
        final ArrayList<byte[]> pendingData = new ArrayList<>();
        final ArrayList<Long> pendingPts = new ArrayList<>();

        VideoPipeline(TSMuxer mux, int w, int h, int dpi) {
            this.mux = mux;
            this.w = w;
            this.h = h;
            this.dpi = dpi;
        }

        void start() throws Exception {
            MediaFormat f = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h);
            f.setInteger(MediaFormat.KEY_BIT_RATE, 8_000_000);
            f.setInteger(MediaFormat.KEY_FRAME_RATE, 30);
            f.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
            f.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                    MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);

            encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC);
            encoder.configure(f, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            input = encoder.createInputSurface();
            encoder.start();

            display = projection.createVirtualDisplay("PlayViewMirror", w, h, dpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, input, null, null);

            drainThread = new Thread(this::drain, "pv-video");
            drainThread.start();
        }

        private void drain() {
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            try {
                while (running) {
                    int idx = encoder.dequeueOutputBuffer(info, 10_000);
                    if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        // Surface-input encoder: format changes carry no data.
                    } else if (idx >= 0) {
                        ByteBuffer b = encoder.getOutputBuffer(idx);
                        if (b != null && info.size > 0) {
                            b.position(info.offset);
                            b.limit(info.offset + info.size);
                            byte[] data = new byte[info.size];
                            b.get(data);

                            if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                                codecConfig = data;
                                flushPending();
                            } else if (codecConfig != null) {
                                flushPending();
                                mux.writeVideo(data, data.length, info.presentationTimeUs);
                            } else {
                                pendingData.add(data);
                                pendingPts.add(info.presentationTimeUs);
                                if (pendingData.size() > 90) { // safety valve
                                    pendingData.clear();
                                    pendingPts.clear();
                                }
                            }
                        }
                        encoder.releaseOutputBuffer(idx, false);
                        if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) break;
                    }
                }
            } catch (Throwable ignored) {
                // Client disconnected or service stopping — session is over.
            }
        }

        /** Writes queued frames, prepending SPS/PPS to the first one. */
        private void flushPending() throws IOException {
            if (pendingData.isEmpty() || codecConfig == null) return;
            for (int i = 0; i < pendingData.size(); i++) {
                byte[] frame = pendingData.get(i);
                long pts = pendingPts.get(i);
                if (i == 0) {
                    byte[] first = new byte[codecConfig.length + frame.length];
                    System.arraycopy(codecConfig, 0, first, 0, codecConfig.length);
                    System.arraycopy(frame, 0, first, codecConfig.length, frame.length);
                    frame = first;
                }
                mux.writeVideo(frame, frame.length, pts);
            }
            pendingData.clear();
            pendingPts.clear();
        }

        void stop() {
            try { if (display != null) display.release(); } catch (Exception ignored) { }
            try { if (input != null) input.release(); } catch (Exception ignored) { }
            try { if (encoder != null) { encoder.stop(); encoder.release(); } }
            catch (Exception ignored) { }
            try { if (drainThread != null) drainThread.join(500); } catch (Exception ignored) { }
        }
    }

    // ── Audio pipeline (playback capture → AAC → TSMuxer) ───────────────────

    private final class AudioPipeline {
        final TSMuxer mux;
        AudioRecord record;
        MediaCodec encoder;
        Thread feedThread;
        Thread drainThread;
        volatile boolean stopping;

        AudioPipeline(TSMuxer mux) {
            this.mux = mux;
        }

        void start() {
            try {
                AudioPlaybackCaptureConfiguration cfg =
                        new AudioPlaybackCaptureConfiguration.Builder(projection)
                                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                                .build();
                AudioFormat af = new AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(44100)
                        .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                        .build();
                int minBuf = AudioRecord.getMinBufferSize(44100,
                        AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
                record = new AudioRecord.Builder()
                        .setAudioFormat(af)
                        .setBufferSizeInBytes(Math.max(minBuf, 64 * 1024))
                        .setAudioPlaybackCaptureConfig(cfg)
                        .build();

                MediaFormat f = MediaFormat.createAudioFormat(
                        MediaFormat.MIMETYPE_AUDIO_AAC, 44100, 2);
                f.setInteger(MediaFormat.KEY_AAC_PROFILE,
                        MediaCodecInfo.CodecProfileLevel.AACObjectLC);
                f.setInteger(MediaFormat.KEY_BIT_RATE, 128_000);
                f.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 64 * 1024);

                encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC);
                encoder.configure(f, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                encoder.start();
                record.startRecording();

                feedThread = new Thread(this::feed, "pv-audio-in");
                drainThread = new Thread(this::drain, "pv-audio-out");
                feedThread.start();
                drainThread.start();
            } catch (Throwable ignored) {
                stop();
            }
        }

        private void feed() {
            ByteBuffer buf = ByteBuffer.allocateDirect(4096);
            try {
                while (running && !stopping) {
                    buf.clear();
                    int n = record.read(buf, buf.capacity());
                    if (n < 0) break;
                    if (n == 0) continue;

                    int idx = encoder.dequeueInputBuffer(50_000);
                    if (idx < 0) continue;
                    ByteBuffer in = encoder.getInputBuffer(idx);
                    if (in == null) continue;

                    buf.position(0);
                    buf.limit(n);
                    in.clear();
                    in.put(buf);

                    long pts = System.nanoTime() / 1000;
                    encoder.queueInputBuffer(idx, 0, n, pts, 0);
                }
                // End of stream so the drain loop finishes cleanly.
                int idx = encoder.dequeueInputBuffer(100_000);
                if (idx >= 0) {
                    encoder.queueInputBuffer(idx, 0, 0,
                            System.nanoTime() / 1000, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                }
            } catch (Throwable ignored) {
                // Recording stopped or service shutting down.
            }
        }

        private void drain() {
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            try {
                while (running && !stopping) {
                    int idx = encoder.dequeueOutputBuffer(info, 10_000);
                    if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        // Format is known: AAC-LC 44.1 kHz stereo (matches the config).
                    } else if (idx >= 0) {
                        ByteBuffer b = encoder.getOutputBuffer(idx);
                        if (b != null && info.size > 0
                                && (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0) {
                            b.position(info.offset);
                            b.limit(info.offset + info.size);
                            byte[] frame = new byte[info.size];
                            b.get(frame);

                            mux.writeAudio(addAdtsHeader(frame, frame.length), frame.length + 7,
                                    info.presentationTimeUs);
                        }
                        encoder.releaseOutputBuffer(idx, false);
                        if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) break;
                    }
                }
            } catch (Throwable ignored) {
                // Client disconnected or service stopping.
            }
        }

        /** 7-byte ADTS header for AAC-LC, 44.1 kHz, stereo (no CRC). */
        private static byte[] addAdtsHeader(byte[] frame, int esLen) {
            int len = esLen + 7;
            byte[] h = new byte[7];
            h[0] = (byte) 0xFF;
            h[1] = (byte) 0xF1;               // MPEG-4, no CRC
            h[2] = (byte) 0x50;               // profile AAC-LC, 44.1 kHz, ch upper bit
            h[3] = (byte) (0x80 | ((len >> 11) & 0x03)); // stereo bit | length[13:11]
            h[4] = (byte) ((len >> 3) & 0xFF);
            h[5] = (byte) (((len & 0x07) << 5) | 0x1F);
            h[6] = (byte) 0xFC;
            byte[] out = new byte[len];
            System.arraycopy(h, 0, out, 0, 7);
            System.arraycopy(frame, 0, out, 7, esLen);
            return out;
        }

        void stop() {
            stopping = true;
            try { if (record != null) record.stop(); } catch (Exception ignored) { }
            try { if (record != null) record.release(); } catch (Exception ignored) { }
            try { if (encoder != null) { encoder.stop(); encoder.release(); } }
            catch (Exception ignored) { }
            try { if (feedThread != null) feedThread.join(500); } catch (Exception ignored) { }
            try { if (drainThread != null) drainThread.join(500); } catch (Exception ignored) { }
        }
    }

    // ── Notification ────────────────────────────────────────────────────────

    private void startForegroundWithNotice() {
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(new NotificationChannel(CHANNEL_ID,
                    "Screen streaming", NotificationManager.IMPORTANCE_LOW));
        }

        Notification.Builder b = Build.VERSION.SDK_INT >= 26
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);
        Notification n = b
                .setContentTitle("PlayView Stream")
                .setContentText("Streaming this screen to PlayView (port " + PORT + ")")
                .setSmallIcon(android.R.drawable.ic_media_play)
                .setOngoing(true)
                .build();

        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(1, n);
        }
    }
}
