package com.clgame2phone;

import android.os.SystemClock;
import android.view.InputDevice;
import android.view.MotionEvent;

import java.io.DataInputStream;
import java.io.EOFException;
import java.lang.reflect.Method;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;

/**
 * Chạy bên trong BlueStacks bằng app_process.
 * Nhận gói touch từ PC và bơm MotionEvent multi-touch vào Android guest.
 *
 * Gói: int32 length (big endian), payload:
 *   [action u8][count u8][changedIndex u8]
 *   count * [pointerId u8][x f32][y f32]
 * action: 0=DOWN 1=MOVE 2=UP 3=CANCEL 4=POINTER_DOWN 5=POINTER_UP
 */
public final class TouchServer {

    private static Object inputManager;
    private static Method injectInputEvent;

    public static void main(String[] args) {
        int port = args.length > 0 ? Integer.parseInt(args[0]) : 27184;
        try {
            Class<?> cls = Class.forName("android.hardware.input.InputManager");
            Method getInstance = cls.getMethod("getInstance");
            inputManager = getInstance.invoke(null);
            injectInputEvent = cls.getMethod(
                    "injectInputEvent", android.view.InputEvent.class, int.class);

            ServerSocket server = new ServerSocket(port, 50, InetAddress.getByName("127.0.0.1"));
            System.out.println("TouchServer ready on " + port);

            while (true) {
                Socket socket = server.accept();
                System.out.println("PC connected");
                try {
                    handle(socket);
                } catch (Exception e) {
                    e.printStackTrace();
                } finally {
                    try {
                        socket.close();
                    } catch (Exception ignored) {
                    }
                }
            }
        } catch (Throwable t) {
            t.printStackTrace();
            System.exit(1);
        }
    }

    private static void handle(Socket socket) throws Exception {
        DataInputStream in = new DataInputStream(socket.getInputStream());
        long downTime = 0;

        while (true) {
            int length;
            try {
                length = in.readInt();
            } catch (EOFException eof) {
                break;
            }
            if (length <= 0 || length > 8192) {
                break;
            }

            byte[] buf = new byte[length];
            in.readFully(buf);

            int action = buf[0] & 0xFF;
            int count = buf[1] & 0xFF;
            int changed = buf[2] & 0xFF;
            if (count <= 0 || count > 10) {
                continue;
            }

            MotionEvent.PointerProperties[] props = new MotionEvent.PointerProperties[count];
            MotionEvent.PointerCoords[] coords = new MotionEvent.PointerCoords[count];
            int off = 3;
            for (int i = 0; i < count; i++) {
                int id = buf[off] & 0xFF;
                float x = readFloat(buf, off + 1);
                float y = readFloat(buf, off + 5);
                off += 9;

                MotionEvent.PointerProperties pp = new MotionEvent.PointerProperties();
                pp.id = id;
                pp.toolType = MotionEvent.TOOL_TYPE_FINGER;
                props[i] = pp;

                MotionEvent.PointerCoords pc = new MotionEvent.PointerCoords();
                pc.x = x;
                pc.y = y;
                pc.pressure = 1f;
                pc.size = 1f;
                coords[i] = pc;
            }

            long now = SystemClock.uptimeMillis();
            int meAction;
            switch (action) {
                case 0:
                    meAction = MotionEvent.ACTION_DOWN;
                    downTime = now;
                    break;
                case 2:
                    meAction = MotionEvent.ACTION_UP;
                    break;
                case 3:
                    meAction = MotionEvent.ACTION_CANCEL;
                    break;
                case 4:
                    meAction = MotionEvent.ACTION_POINTER_DOWN
                            | (changed << MotionEvent.ACTION_POINTER_INDEX_SHIFT);
                    break;
                case 5:
                    meAction = MotionEvent.ACTION_POINTER_UP
                            | (changed << MotionEvent.ACTION_POINTER_INDEX_SHIFT);
                    break;
                default:
                    meAction = MotionEvent.ACTION_MOVE;
                    break;
            }

            try {
                MotionEvent event = MotionEvent.obtain(
                        downTime, now, meAction, count, props, coords,
                        0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0);
                Object result = injectInputEvent.invoke(inputManager, event, 0);
                System.out.println("inject action=" + action + " result=" + result);
                event.recycle();
            } catch (Throwable t) {
                System.out.println("inject failed: " + t);
            }

            if (action == 2 || action == 3) {
                downTime = 0;
            }
        }
    }

    private static float readFloat(byte[] b, int off) {
        int bits = ((b[off] & 0xFF) << 24)
                | ((b[off + 1] & 0xFF) << 16)
                | ((b[off + 2] & 0xFF) << 8)
                | (b[off + 3] & 0xFF);
        return Float.intBitsToFloat(bits);
    }
}
