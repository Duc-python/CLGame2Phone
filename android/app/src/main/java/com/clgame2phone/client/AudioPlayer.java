package com.clgame2phone.client;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.util.Log;

import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * Nhận PCM 48kHz stereo 16-bit từ PC và phát trực tiếp.
 * Không dùng codec để tránh lỗi decoder trên máy Android Go.
 */
public final class AudioPlayer {

    private static final String TAG = "CLGAudio";
    private static final int SAMPLE_RATE = 48000;
    private static final int PACKET_BYTES = 960 * 2 * 2; // 20ms

    private final LinkedBlockingQueue<byte[]> queue = new LinkedBlockingQueue<>(16);
    private volatile boolean running;
    private Thread thread;
    private AudioTrack track;

    public void start() {
        if (running) {
            return;
        }
        running = true;
        thread = new Thread(this::loop, "audio");
        thread.setDaemon(true);
        thread.start();
    }

    public void feed(byte[] pcm) {
        if (!queue.offer(pcm)) {
            queue.poll();
            queue.offer(pcm);
        }
    }

    private void loop() {
        try {
            int minBuffer = AudioTrack.getMinBufferSize(
                    SAMPLE_RATE,
                    AudioFormat.CHANNEL_OUT_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT);
            int bufferSize = Math.max(minBuffer, PACKET_BYTES);

            AudioAttributes attributes = new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_GAME)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build();
            AudioFormat format = new AudioFormat.Builder()
                    .setSampleRate(SAMPLE_RATE)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                    .build();

            track = new AudioTrack.Builder()
                    .setAudioAttributes(attributes)
                    .setAudioFormat(format)
                    .setBufferSizeInBytes(bufferSize)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                    .build();
            track.play();
            Log.d(TAG, "started, buffer=" + bufferSize);
        } catch (Exception ex) {
            Log.e(TAG, "init failed", ex);
            running = false;
            release();
            return;
        }

        while (running) {
            try {
                byte[] pcm = queue.poll(50, TimeUnit.MILLISECONDS);
                if (pcm != null) {
                    track.write(pcm, 0, pcm.length);
                }
            } catch (InterruptedException ignored) {
            } catch (Exception ex) {
                Log.e(TAG, "write failed", ex);
                break;
            }
        }

        release();
    }

    public void stop() {
        running = false;
        Thread t = thread;
        if (t != null) {
            t.interrupt();
            try {
                t.join(500);
            } catch (InterruptedException ignored) {
            }
        }
        release();
    }

    private void release() {
        try {
            if (track != null) {
                track.stop();
                track.release();
            }
        } catch (Exception ignored) {
        }
        track = null;
    }
}
