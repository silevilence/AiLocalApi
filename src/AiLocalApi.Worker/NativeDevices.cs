using System.Runtime.InteropServices;

namespace AiLocalApi.Worker;

// Read the registry of the already loaded ggml runtime, not the DLL's build label.
// A Vulkan/CUDA build can initialize a CPU context when no GPU device exists.
internal static class NativeDevices
{
    public static string[] Gpus()
    {
        var registry = GetModuleHandle("ggml-whisper.dll");
        var core = GetModuleHandle("ggml-base-whisper.dll");
        if (registry == IntPtr.Zero || core == IntPtr.Zero) throw new InvalidOperationException("无法读取实际 GPU 设备注册表。");
        var count = Export<Count>(registry, "ggml_backend_dev_count");
        var get = Export<Get>(registry, "ggml_backend_dev_get");
        var type = Export<DeviceType>(core, "ggml_backend_dev_type");
        var name = Export<DeviceName>(core, "ggml_backend_dev_name");
        var devices = new List<string>();
        for (nuint i = 0; i < count(); i++)
        {
            var device = get(i);
            if (type(device) is 1 or 2) devices.Add(Marshal.PtrToStringUTF8(name(device)) ?? "GPU");
        }
        return devices.ToArray();
    }
    private static T Export<T>(IntPtr library, string symbol) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandle(string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint Count();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Get(nuint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceType(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DeviceName(IntPtr device);
}
