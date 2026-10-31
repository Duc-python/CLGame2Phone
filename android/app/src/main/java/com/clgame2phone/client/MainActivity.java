package com.clgame2phone.client;

import android.app.Activity;
import android.graphics.Color;
import android.os.Bundle;
import android.util.DisplayMetrics;
import android.view.Gravity;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.TextView;

public final class MainActivity extends Activity implements VideoClient.Listener {

    private VideoClient client;
    private SurfaceView surfaceView;
    private TextView statusView;

    private final Runnable hideStatus = () -> {
        if (statusView != null) {
            statusView.animate().alpha(0f).setDuration(500).start();
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        hideSystemUi();

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);

        surfaceView = new SurfaceView(this);

        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT);
        root.addView(surfaceView, lp);

        statusView = new TextView(this);
        statusView.setTextColor(Color.WHITE);
        statusView.setTextSize(12f);
        statusView.setPadding(20, 14, 20, 14);
        statusView.setBackgroundColor(0x66000000);
        statusView.setText("CLGame2Phone\nĐang chờ PC...");
        statusView.setClickable(false);
        statusView.setFocusable(false);
        FrameLayout.LayoutParams slp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP | Gravity.START);
        slp.setMargins(16, 16, 16, 16);
        root.addView(statusView, slp);

        setContentView(root);

        surfaceView.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                client = new VideoClient(holder.getSurface(), MainActivity.this);
                client.start();
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                if (client != null) {
                    client.stop();
                    client = null;
                }
            }
        });

        surfaceView.setOnTouchListener((v, event) -> {
            if (client != null) {
                client.sendTouch(event, surfaceView.getWidth(), surfaceView.getHeight());
            }
            return true;
        });
    }

    private void hideSystemUi() {
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                        | View.SYSTEM_UI_FLAG_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            hideSystemUi();
        }
    }

    @Override
    public void onStatus(final String text) {
        runOnUiThread(() -> {
            statusView.setText(text);
            statusView.setAlpha(1f);
            statusView.removeCallbacks(hideStatus);
            statusView.postDelayed(hideStatus, 4000);
        });
    }

    @Override
    protected void onDestroy() {
        if (client != null) {
            client.stop();
            client = null;
        }
        super.onDestroy();
    }
}
