package com.clgame2phone.client;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Build;
import android.util.Log;
import android.view.MotionEvent;
import android.view.Surface;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

public final class VideoClient {

    public interface Listener {
        void onStatus(String text);
    }

    private static final String HOST = "127.0.0.1";
    private static final int PORT = 27183;

    private static final int PKT_VIDEO = 1;
    private static final int PKT_CONFIG = 2;
    private static final int PKT_PONG = 3;

    private static final int PKT_TOUCH = 1;
    private static final int PKT_PING = 2;
    private static final int PKT_KEY = 4;
    private static final int PKT_MOUSE = 5;
    private static final int PKT_AUDIO = 6;

    private final Surface surface;
    private final Listener listener;
    private volatile boolean running;
    private Thread connectThread;
    private volatile Session session;

    public VideoClient(Surface surface, Listener listener) {
        this.surface = surface;
        this.listener = listener;
    }

    public void start() {
        if (running) {
            return;
        }
        running = true;
        connectThread = new Thread(this::connectLoop, "connect");
        connectThread.setDaemon(true);
        connectThread.start();
    }

    public void stop() {
        running = false;
        Session s = session;
        if (s != null) {
            s.close();
        }
        if (connectThread != null) {
            connectThread.interrupt();
        }
    }

    public void sendTouch(MotionEvent e, int viewW, int viewH) {
        Session s = session;
        if (s != null) {
            s.sendTouch(e, viewW, viewH);
        }
    }

    public void sendKey(int vk, boolean down) {
        Session s = session;
        if (s != null) {
            s.sendKey(vk, down);
        }
    }

    public void sendMouse(int action, float x, float y) {
        Session s = session;
        if (s != null) {
            s.sendMouse(action, x, y);
        }
    }

    private void connectLoop() {
        while (running) {
            Socket socket = new Socket();
            try {
                listener.onStatus("CLGame2Phone\nĐang kết nối PC...");
                socket.setTcpNoDelay(true);
                socket.setSoTimeout(0);
                socket.connect(new InetSocketAddress(HOST, PORT), 1500);

                Session s = new Session(socket);
                session = s;
                s.start();
                listener.onStatus("CLGame2Phone\nĐã kết nối, chờ hình...");
                s.join();
            } catch (Exception ex) {
                listener.onStatus("CLGame2Phone\nChờ PC...\n" + ex.getMessage());
            } finally {
                session = null;
                try {
                    socket.close();
                } catch (IOException ignored) {
                }
            }
            if (!running) {
                break;
            }
            try {
                Thread.sleep(700);
            } catch (InterruptedException ignored) {
            }
        }
    }

    private final class Session {

        private final Socket socket;
        private final DataInputStream in;
        private final OutputStream out;
        private final LinkedBlockingQueue<Nal> queue = new LinkedBlockingQueue<>(1);
        private final LinkedBlockingQueue<Packet> inputQueue = new LinkedBlockingQueue<>(64);
        private final Object sendLock = new Object();

        private volatile boolean alive = true;
        private volatile boolean codecReady = false;
        private volatile boolean waitingKey = false;
        private long dropped;
        private MediaCodec codec;
        private byte[] sps;
        private byte[] pps;
        private int width = 1280;
        private int height = 720;
        private int fps = 60;
        private long lastPtsUs;

        private Thread readerThread;
        private Thread decoderThread;
        private Thread pingThread;
        private Thread senderThread;
        private final AudioPlayer audio = new AudioPlayer();
        private long frames;
        private long lastStats;

        Session(Socket socket) throws IOException {
            this.socket = socket;
            this.in = new DataInputStream(new BufferedInputStream(socket.getInputStream(), 64 * 1024));
            this.out = new BufferedOutputStream(socket.getOutputStream(), 16 * 1024);
        }

        void start() {
            readerThread = new Thread(this::readLoop, "reader");
            readerThread.setDaemon(true);
            readerThread.start();

            decoderThread = new Thread(this::decodeLoop, "decoder");
            decoderThread.setDaemon(true);
            decoderThread.start();

            pingThread = new Thread(this::pingLoop, "ping");
            pingThread.setDaemon(true);
            pingThread.start();

            senderThread = new Thread(this::sendLoop, "sender");
            senderThread.setDaemon(true);
            senderThread.start();

            audio.start();
        }

        void join() {
            try {
                readerThread.join();
            } catch (InterruptedException ignored) {
            }
            close();
        }

        void close() {
            alive = false;
            codecReady = false;
            try {
                socket.close();
            } catch (IOException ignored) {
            }
            if (decoderThread != null) {
                decoderThread.interrupt();
            }
            if (pingThread != null) {
                pingThread.interrupt();
            }
            if (senderThread != null) {
                senderThread.interrupt();
            }
            audio.stop();
            releaseCodec();
        }

        private void readLoop() {
            try {
                while (alive && running) {
                    int len;
                    try {
                        len = in.readInt();
                    } catch (EOFException eof) {
                        break;
                    }
                    if (len <= 0 || len > 4 * 1024 * 1024) {
                        break;
                    }
                    int type = in.readUnsignedByte();
                    byte[] payload = new byte[len - 1];
                    in.readFully(payload);

                    if (type == PKT_CONFIG) {
                        if (payload.length >= 12) {
                            width = readInt(payload, 0);
                            height = readInt(payload, 4);
                            fps = readInt(payload, 8);
                            listener.onStatus("CLGame2Phone\n" + width + "x" + height + " @" + fps + "fps");
                        }
                    } else if (type == PKT_VIDEO) {
                        if (payload.length < 10) {
                            continue;
                        }
                        int nalType = payload[0] & 0x1F;
                        long pts = readLong(payload, 1);
                        byte[] nal = Arrays.copyOfRange(payload, 9, payload.length);

                        if (nalType == 7) {
                            sps = nal;
                            tryConfigure();
                        } else if (nalType == 8) {
                            pps = nal;
                            tryConfigure();
                        } else if (nalType == 5) {
                            if (codecReady) {
                                waitingKey = false;
                                queue.clear();
                                queue.offer(new Nal(nal, pts));
                            }
                        } else if (nalType == 1) {
                            if (codecReady && !waitingKey) {
                                if (!queue.offer(new Nal(nal, pts))) {
                                    // render-only-last-frame: drop oldest, keep newest
                                    queue.poll();
                                    queue.offer(new Nal(nal, pts));
                                }
                            }
                        }
                    } else if (type == PKT_AUDIO) {
                        if (payload.length > 0) {
                            audio.feed(payload);
                        }
                    } else if (type == PKT_PONG) {
                        // RTT display can be added later.
                    }
                }
            } catch (IOException ignored) {
            } finally {
                close();
            }
        }

        private void pingLoop() {
            try {
                while (alive && running) {
                    Thread.sleep(1000);
                    long now = System.nanoTime();
                    byte[] p = new byte[8];
                    writeLong(p, 0, now);
                    synchronized (sendLock) {
                        writePacket(out, PKT_PING, p);
                        out.flush();
                    }
                }
            } catch (Exception ignored) {
            }
        }

        private synchronized void tryConfigure() {
            if (codecReady || sps == null || pps == null) {
                return;
            }
            try {
                MediaCodec c = MediaCodec.createDecoderByType("video/avc");
                MediaFormat fmt = MediaFormat.createVideoFormat("video/avc", width, height);
                fmt.setByteBuffer("csd-0", ByteBuffer.wrap(sps));
                fmt.setByteBuffer("csd-1", ByteBuffer.wrap(pps));
                if (Build.VERSION.SDK_INT >= 30) {
                    fmt.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
                }
                fmt.setInteger(MediaFormat.KEY_PRIORITY, 0);
                fmt.setInteger(MediaFormat.KEY_OPERATING_RATE, fps);
                c.configure(fmt, surface, null, 0);
                c.start();
                codec = c;
                codecReady = true;
                lastPtsUs = 0;
                listener.onStatus("CLGame2Phone\n" + width + "x" + height + " @" + fps + "fps");
            } catch (Exception ex) {
                listener.onStatus("CLGame2Phone\nDecoder lỗi: " + ex.getMessage());
                codecReady = false;
                releaseCodec();
            }
        }

        private void decodeLoop() {
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            while (alive && running) {
                try {
                    Nal nal = queue.poll(10, TimeUnit.MILLISECONDS);
                    if (nal != null && codecReady) {
                        feed(nal);
                    }
                    if (codecReady) {
                        while (true) {
                            int outIndex = codec.dequeueOutputBuffer(info, 0);
                            if (outIndex >= 0) {
                                codec.releaseOutputBuffer(outIndex, true);
                                frames++;
                            } else if (outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                                // Ignore.
                            } else {
                                break;
                            }
                        }
                    }
                    long now = System.currentTimeMillis();
                    if (now - lastStats >= 1000) {
                        lastStats = now;
                        Log.d("CLGVideo", "fps=" + frames + " q=" + queue.size()
                                + " drop=" + dropped + " waitKey=" + waitingKey);
                        frames = 0;
                    }
                } catch (InterruptedException ignored) {
                } catch (Exception ex) {
                    listener.onStatus("CLGame2Phone\nDecode lỗi: " + ex.getMessage());
                    codecReady = false;
                    releaseCodec();
                }
            }
        }

        private void feed(Nal nal) {
            try {
                int inIndex = codec.dequeueInputBuffer(5000);
                if (inIndex < 0) {
                    return;
                }
                ByteBuffer buf = codec.getInputBuffer(inIndex);
                if (buf == null) {
                    return;
                }
                buf.clear();
                buf.put(nal.data);
                long pts = nal.ptsUs;
                if (pts <= lastPtsUs) {
                    pts = lastPtsUs + 16666;
                }
                lastPtsUs = pts;
                codec.queueInputBuffer(inIndex, 0, nal.data.length, pts, 0);
            } catch (Exception ignored) {
            }
        }

        private void releaseCodec() {
            MediaCodec c = codec;
            codec = null;
            if (c != null) {
                try {
                    c.stop();
                } catch (Exception ignored) {
                }
                try {
                    c.release();
                } catch (Exception ignored) {
                }
            }
        }

        void sendTouch(MotionEvent e, int viewW, int viewH) {
            if (!alive || viewW <= 0 || viewH <= 0) {
                return;
            }
            int action = e.getActionMasked();
            int outAction;
            switch (action) {
                case MotionEvent.ACTION_DOWN:
                    outAction = 0;
                    break;
                case MotionEvent.ACTION_MOVE:
                    outAction = 1;
                    break;
                case MotionEvent.ACTION_UP:
                    outAction = 2;
                    break;
                case MotionEvent.ACTION_CANCEL:
                    outAction = 3;
                    break;
                case MotionEvent.ACTION_POINTER_DOWN:
                    outAction = 4;
                    break;
                case MotionEvent.ACTION_POINTER_UP:
                    outAction = 5;
                    break;
                default:
                    return;
            }

            int count = e.getPointerCount();
            byte changed = (outAction == 4 || outAction == 5)
                    ? (byte) e.getActionIndex()
                    : (byte) 0;
            byte[] payload = new byte[3 + count * 9];
            payload[0] = (byte) outAction;
            payload[1] = (byte) count;
            payload[2] = changed;
            int off = 3;
            for (int i = 0; i < count; i++) {
                float x = e.getX(i) / (float) viewW;
                float y = e.getY(i) / (float) viewH;
                if (x < 0) x = 0;
                if (x > 1) x = 1;
                if (y < 0) y = 0;
                if (y > 1) y = 1;
                payload[off++] = (byte) e.getPointerId(i);
                writeFloat(payload, off, x);
                off += 4;
                writeFloat(payload, off, y);
                off += 4;
            }

            enqueue(PKT_TOUCH, payload);
        }

        void sendKey(int vk, boolean down) {
            if (!alive) {
                return;
            }
            byte[] payload = new byte[5];
            payload[0] = (byte) (down ? 1 : 0);
            payload[1] = (byte) (vk >>> 24);
            payload[2] = (byte) (vk >>> 16);
            payload[3] = (byte) (vk >>> 8);
            payload[4] = (byte) vk;
            enqueue(PKT_KEY, payload);
        }

        void sendMouse(int action, float x, float y) {
            if (!alive) {
                return;
            }
            byte[] payload = new byte[9];
            payload[0] = (byte) action;
            writeFloat(payload, 1, x);
            writeFloat(payload, 5, y);
            enqueue(PKT_MOUSE, payload);
        }

        private void enqueue(int type, byte[] payload) {
            Packet packet = new Packet(type, payload);
            if (!inputQueue.offer(packet)) {
                inputQueue.poll();
                inputQueue.offer(packet);
            }
        }

        private void sendLoop() {
            try {
                while (alive && running) {
                    Packet packet = inputQueue.take();
                    synchronized (sendLock) {
                        writePacket(out, packet.type, packet.payload);
                        out.flush();
                    }
                }
            } catch (InterruptedException ignored) {
            } catch (IOException ex) {
                android.util.Log.e("CLGTouch", "sendLoop IOException", ex);
            } catch (Exception ex) {
                android.util.Log.e("CLGTouch", "sendLoop error", ex);
            }
        }
    }

    private static final class Packet {
        final int type;
        final byte[] payload;

        Packet(int type, byte[] payload) {
            this.type = type;
            this.payload = payload;
        }
    }

    private static final class Nal {
        final byte[] data;
        final long ptsUs;

        Nal(byte[] data, long ptsUs) {
            this.data = data;
            this.ptsUs = ptsUs;
        }
    }

    private static void writePacket(OutputStream out, int type, byte[] payload) throws IOException {
        int len = payload.length + 1;
        out.write((len >>> 24) & 0xFF);
        out.write((len >>> 16) & 0xFF);
        out.write((len >>> 8) & 0xFF);
        out.write(len & 0xFF);
        out.write(type);
        out.write(payload);
    }

    private static int readInt(byte[] b, int off) {
        return ((b[off] & 0xFF) << 24)
                | ((b[off + 1] & 0xFF) << 16)
                | ((b[off + 2] & 0xFF) << 8)
                | (b[off + 3] & 0xFF);
    }

    private static long readLong(byte[] b, int off) {
        long v = 0;
        for (int i = 0; i < 8; i++) {
            v = (v << 8) | (b[off + i] & 0xFFL);
        }
        return v;
    }

    private static void writeFloat(byte[] b, int off, float v) {
        int bits = Float.floatToIntBits(v);
        b[off] = (byte) (bits >>> 24);
        b[off + 1] = (byte) (bits >>> 16);
        b[off + 2] = (byte) (bits >>> 8);
        b[off + 3] = (byte) bits;
    }

    private static void writeLong(byte[] b, int off, long v) {
        for (int i = 7; i >= 0; i--) {
            b[off + i] = (byte) v;
            v >>>= 8;
        }
    }
}
