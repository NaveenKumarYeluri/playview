package com.playview.streamer;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Collections;
import java.util.List;

public class MainActivity extends Activity {

    private static final int REQUEST_CAPTURE = 1001;
    private static final int REQUEST_NOTIFICATIONS = 2002;

    private TextView status;
    private android.widget.CheckBox audioCheck;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        CrashCatcher.install(this);

        int pad = dp(24);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(pad, pad, pad, pad);

        TextView title = new TextView(this);
        title.setText("PlayView Stream");
        title.setTextSize(24);
        root.addView(title);

        TextView help = new TextView(this);
        help.setText("Streams this phone's screen (video + sound) to PlayView on your laptop "
                + "as a standard video stream.\n\n"
                + "USB: plug the phone in (USB debugging on), tap Start streaming, then in "
                + "PlayView click \"Start stream (USB)\".\n\n"
                + "Wi-Fi: tap Start streaming, then enter this phone's IP in PlayView and click "
                + "\"Start stream (Wi-Fi)\".\n\n"
                + "Tip: rotate the phone to the orientation you want BEFORE starting — "
                + "the stream keeps the orientation it started with.\n\n"
                + "Keep this app's notification while streaming.");
        help.setTextSize(14);
        help.setPadding(0, dp(12), 0, dp(12));
        root.addView(help);

        Button start = new Button(this);
        start.setText("Start streaming");
        start.setOnClickListener(v -> beginStart());
        root.addView(start);

        Button stop = new Button(this);
        stop.setText("Stop");
        stop.setOnClickListener(v ->
                startService(new Intent(this, StreamService.class).setAction("stop")));
        root.addView(stop);

        audioCheck = new android.widget.CheckBox(this);
        if (Build.VERSION.SDK_INT >= 29) {
            audioCheck.setText("Stream audio (internal device sound)");
            audioCheck.setChecked(true);
        } else {
            audioCheck.setText("Capture audio via Microphone\n(Android 9: internal audio requires Android 10+.\nLeave unchecked and use Bluetooth Audio for crystal-clear sound)");
            audioCheck.setChecked(false);
        }
        audioCheck.setPadding(0, dp(8), 0, dp(4));
        root.addView(audioCheck);

        status = new TextView(this);
        status.setTextSize(13);
        status.setPadding(0, dp(14), 0, 0);
        android.widget.ScrollView scroller = new android.widget.ScrollView(this);
        scroller.addView(status);
        root.addView(scroller);

        setContentView(root);
    }

    @Override
    protected void onResume() {
        super.onResume();
        updateStatus();
    }

    private void beginStart() {
        java.util.ArrayList<String> perms = new java.util.ArrayList<>();
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            perms.add(Manifest.permission.RECORD_AUDIO);
        }
        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                   != PackageManager.PERMISSION_GRANTED) {
            perms.add(Manifest.permission.POST_NOTIFICATIONS);
        }
        if (!perms.isEmpty()) {
            requestPermissions(perms.toArray(new String[0]), REQUEST_NOTIFICATIONS);
            return;
        }
        requestCapture();
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        requestCapture();
    }

    private void requestCapture() {
        MediaProjectionManager mpm =
                (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        startActivityForResult(mpm.createScreenCaptureIntent(), REQUEST_CAPTURE);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQUEST_CAPTURE || resultCode != RESULT_OK || data == null) {
            updateStatus();
            return;
        }

        Intent svc = new Intent(this, StreamService.class);
        svc.putExtra("resultCode", resultCode);
        svc.putExtra("data", data);
        svc.putExtra("enableAudio", audioCheck == null || audioCheck.isChecked());
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(svc);
        else startService(svc);
        updateStatus();
    }

    private void updateStatus() {
        String state = StreamService.isActive()
                ? "Streaming — open PlayView on the laptop and start the stream."
                : "Not streaming.";
        status.setText(state + "\n\nThis phone's IP (for Wi-Fi): " + localIp()
                + "\nPort: " + StreamService.PORT);

        String crash = CrashCatcher.lastCrash(this);
        if (crash != null) status.setText(status.getText() + "\n\n── LAST CRASH ──\n" + crash);
    }

    private String localIp() {
        try {
            for (NetworkInterface nif : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!nif.isUp() || nif.isLoopback()) continue;
                for (InetAddress addr : Collections.list(nif.getInetAddresses())) {
                    if (addr.getAddress().length == 4 && !addr.isLoopbackAddress())
                        return addr.getHostAddress();
                }
            }
        } catch (Exception ignored) { }
        return "(connect to Wi-Fi to see it)";
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
