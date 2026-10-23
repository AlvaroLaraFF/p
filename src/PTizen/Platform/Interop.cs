// Minimal Evas / Evas-GL / GLES interop (adapted from SkiaSharp.Views.Tizen, MIT) so we can build our own Skia surface view.
using System;
using System.Runtime.InteropServices;

namespace PTizen.Platform
{
    internal static class Libs
    {
        public const string Evas = "libevas.so.1";
        public const string Gles = "libGLESv2.so";
        public const string Egl = "libEGL.so.1";
        public const string Dl = "libdl.so.2";
        public const string Elm = "libelementary.so.1";
    }

    internal static class Elm
    {
        [DllImport(Libs.Elm)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool elm_win_focus_get(IntPtr win);
    }

    internal static class Evas
    {
        [DllImport(Libs.Evas)] public static extern IntPtr evas_object_evas_get(IntPtr obj);
        [DllImport(Libs.Evas)] public static extern IntPtr evas_gl_new(IntPtr evas);
        [DllImport(Libs.Evas)] public static extern void evas_gl_free(IntPtr evasGl);
        [DllImport(Libs.Evas)] public static extern IntPtr evas_gl_context_create(IntPtr evasGl, IntPtr share);
        [DllImport(Libs.Evas)] public static extern IntPtr evas_gl_context_version_create(IntPtr evasGl, IntPtr share, int version);
        [DllImport(Libs.Evas)] public static extern void evas_gl_context_destroy(IntPtr evasGl, IntPtr ctx);
        [DllImport(Libs.Evas)] public static extern IntPtr evas_gl_surface_create(IntPtr evasGl, IntPtr config, int w, int h);
        [DllImport(Libs.Evas)] public static extern void evas_gl_surface_destroy(IntPtr evasGl, IntPtr surf);
        [DllImport(Libs.Evas)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool evas_gl_native_surface_get(IntPtr evasGl, IntPtr surf, out NativeSurfaceOpenGL ns);
        [DllImport(Libs.Evas)] public static extern IntPtr evas_gl_proc_address_get(IntPtr evasGl, string name);
        [DllImport(Libs.Evas)] public static extern IntPtr evas_gl_api_get(IntPtr evasGl);
        [DllImport(Libs.Evas)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool evas_gl_make_current(IntPtr evasGl, IntPtr surf, IntPtr ctx);

        [DllImport(Libs.Evas)] public static extern IntPtr evas_object_image_filled_add(IntPtr evas);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_size_set(IntPtr obj, int w, int h);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_data_set(IntPtr obj, IntPtr data);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_data_update_add(IntPtr obj, int x, int y, int w, int h);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_colorspace_set(IntPtr obj, int cspace);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_native_surface_set(IntPtr obj, ref NativeSurfaceOpenGL surf);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_native_surface_set(IntPtr obj, IntPtr zero);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_pixels_dirty_set(IntPtr obj, [MarshalAs(UnmanagedType.U1)] bool dirty);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_pixels_get_callback_set(IntPtr obj, ImagePixelsCallback func, IntPtr data);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_pixels_get_callback_set(IntPtr obj, IntPtr zero, IntPtr data);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_smooth_scale_set(IntPtr obj, [MarshalAs(UnmanagedType.U1)] bool smooth);
        [DllImport(Libs.Evas)] public static extern void evas_object_image_alpha_set(IntPtr obj, [MarshalAs(UnmanagedType.U1)] bool alpha);
        [DllImport(Libs.Evas)] public static extern void evas_object_show(IntPtr obj);
        public delegate void EventCallback(IntPtr data, IntPtr evas, IntPtr eventInfo);
        [DllImport(Libs.Evas)] public static extern void evas_event_callback_add(IntPtr evas, int type, EventCallback func, IntPtr data);
        public const int CallbackRenderFlushPre = 25, CallbackRenderFlushPost = 26, CallbackRenderPre = 30, CallbackRenderPost = 31;
        [DllImport(Libs.Evas)] public static extern void evas_object_del(IntPtr obj);

        public delegate void ImagePixelsCallback(IntPtr data, IntPtr obj);

        public const int ColorspaceArgb8888 = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct Config
        {
            public int color_format;      // 0 RGB_888, 1 RGBA_8888, 2 NO_FBO
            public int depth_bits;        // 0 none, 1 8, 2 16, 3 24, 4 32
            public int stencil_bits;      // 0 none, 1 1, 2 2, 3 4, 4 8, 5 16
            public int options_bits;      // 1 DIRECT, 2 CLIENT_SIDE_ROTATION, 4 THREAD
            public int multisample_bits;  // 0 none, 1 low, 2 med, 3 high
            public int gles_version;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeSurfaceOpenGL
        {
            public uint texture_id, framebuffer_id, internal_format, format, x, y, w, h;
        }
    }

    internal static class Gles
    {
        public const uint GL_FRAMEBUFFER_BINDING = 0x8CA6, GL_STENCIL_BITS = 0x0D57, GL_SAMPLES = 0x80A9, GL_DEPTH_BITS = 0x0D56;
        public const uint GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02, GL_EXTENSIONS = 0x1F03;
        public const uint GL_MAX_TEXTURE_SIZE = 0x0D33;
        public const int GL_COLOR_BUFFER_BIT = 0x4000, GL_STENCIL_BUFFER_BIT = 0x400, GL_DEPTH_BUFFER_BIT = 0x100;

        [DllImport(Libs.Gles)] public static extern void glGetIntegerv(uint pname, out int data);
        [DllImport(Libs.Gles)] public static extern IntPtr glGetString(uint name);
        [DllImport(Libs.Gles)] public static extern void glViewport(int x, int y, int w, int h);
        [DllImport(Libs.Gles)] public static extern void glClear(int mask);
        [DllImport(Libs.Gles)] public static extern void glClearColor(float r, float g, float b, float a);
        [DllImport(Libs.Gles)] public static extern void glFinish();
        [DllImport(Libs.Gles)] public static extern void glFlush();
        [DllImport(Libs.Gles)] public static extern uint glGetError();
        [DllImport(Libs.Gles)] public static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, byte[] pixels);
        public const uint GL_RGBA = 0x1908, GL_UNSIGNED_BYTE = 0x1401;

        public static string Str(uint name) { var p = glGetString(name); return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p); }
    }

    internal static class Egl
    {
        [DllImport(Libs.Egl)] public static extern IntPtr eglGetProcAddress(string name);
    }

    internal static class Dl
    {
        [DllImport(Libs.Dl)] public static extern IntPtr dlopen(string file, int mode);
        [DllImport(Libs.Dl)] public static extern IntPtr dlsym(IntPtr handle, string symbol);
    }
}
