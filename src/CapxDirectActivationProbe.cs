using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class CapxDirectActivationProbe
{
    private static readonly Guid FactoryIid = new Guid("705ECD13-3043-5208-982C-E19C05C64B60");
    private static readonly Guid ProxyIid = new Guid("EB02895C-171F-5085-91C6-B7FE6C691948");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initType);
    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetActivationFactoryDelegate(IntPtr classId, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateInstanceDelegate(IntPtr self, IntPtr instancePath, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBlobDelegate(IntPtr self, out uint size, out IntPtr value);

    private static IntPtr VtableSlot(IntPtr instance, int slot)
    {
        return Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
    }

    private static T Delegate<T>(IntPtr instance, int slot) where T : class
    {
        return Marshal.GetDelegateForFunctionPointer<T>(VtableSlot(instance, slot));
    }

    private static string Hr(int value)
    {
        return "0x" + value.ToString("X8");
    }

    private static string Hex(IntPtr buffer, uint size)
    {
        if (buffer == IntPtr.Zero || size == 0) return String.Empty;
        byte[] bytes = new byte[size];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        StringBuilder result = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) result.Append(value.ToString("x2"));
        return result.ToString();
    }

    public static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: CapxDirectActivationProbe.exe <dll> <endpoint> <output>");
            return 2;
        }

        IntPtr module = IntPtr.Zero;
        IntPtr className = IntPtr.Zero;
        IntPtr endpointName = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        IntPtr created = IntPtr.Zero;
        IntPtr proxy = IntPtr.Zero;
        int initHr = RoInitialize(1);
        try
        {
            SetDllDirectory(Path.GetDirectoryName(args[0]));
            module = LoadLibrary(args[0]);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "LoadLibrary failed");
            IntPtr entryPoint = GetProcAddress(module, "DllGetActivationFactory");
            if (entryPoint == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "DllGetActivationFactory export not found");
            DllGetActivationFactoryDelegate getFactory = Marshal.GetDelegateForFunctionPointer<DllGetActivationFactoryDelegate>(entryPoint);

            int hr = WindowsCreateString("CapxComponent.PropertyStoreProxy", 32, out className);
            if (hr < 0) throw new COMException("WindowsCreateString", hr);
            hr = getFactory(className, out factory);
            if (hr < 0) throw new COMException("DllGetActivationFactory", hr);

            hr = WindowsCreateString(args[1], args[1].Length, out endpointName);
            if (hr < 0) throw new COMException("WindowsCreateString(endpoint)", hr);
            IntPtr createdValue;
            int createHr = Delegate<CreateInstanceDelegate>(factory, 6)(factory, endpointName, out createdValue);
            created = createdValue;
            Guid proxyGuid = ProxyIid;
            int queryHr = Delegate<QueryInterfaceDelegate>(created, 0)(created, ref proxyGuid, out proxy);

            int profileHr = unchecked((int)0x80004001);
            string profile = null;
            if (queryHr >= 0)
            {
                uint size;
                IntPtr buffer;
                profileHr = Delegate<GetBlobDelegate>(proxy, 11)(proxy, out size, out buffer);
                try { if (profileHr >= 0) profile = Hex(buffer, size); }
                finally { if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer); }
            }

            File.WriteAllText(args[2], "{" +
                "\"dllGetActivationFactory\":\"" + Hr(hr) + "\"," +
                "\"createInstance\":\"" + Hr(createHr) + "\"," +
                "\"queryInterface\":\"" + Hr(queryHr) + "\"," +
                "\"getAtmosProfile\":\"" + Hr(profileHr) + "\"," +
                "\"hex\":\"" + (profile ?? String.Empty) + "\"" +
                "}", Encoding.UTF8);
            return queryHr < 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            int nativeError = ex is Win32Exception ? ((Win32Exception)ex).NativeErrorCode : 0;
            File.WriteAllText(args[2], "{\"error\":\"" + ex.GetType().FullName.Replace("\"", "") + "\",\"nativeError\":" + nativeError + ",\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}", Encoding.UTF8);
            return 1;
        }
        finally
        {
            if (proxy != IntPtr.Zero) Marshal.Release(proxy);
            if (created != IntPtr.Zero) Marshal.Release(created);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (endpointName != IntPtr.Zero) WindowsDeleteString(endpointName);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
            if (module != IntPtr.Zero) FreeLibrary(module);
            if (initHr == 0) RoUninitialize();
        }
    }
}
