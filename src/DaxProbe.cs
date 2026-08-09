using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Read-only probe for the DAX WinRT class shipped with Dolby Access.
// The probe intentionally does not call any profile setter.
internal static class DaxProbe
{
    private static readonly Guid ActivationFactoryIid = new Guid("00000035-0000-0000-C000-000000000046");
    private static readonly Guid DolbyDaxIid = new Guid("4378E64E-3782-3C8C-A061-84D43CB11332");

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initType);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateInstanceDelegate(IntPtr self, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetVersionDelegate(IntPtr self, out int v1, out int v2, out int v3, out int v4);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeDelegate(IntPtr self, IntPtr userName, out byte result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIntDelegate(IntPtr self, out int result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBoolDelegate(IntPtr self, out byte result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStringDelegate(IntPtr self, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenRpcDelegate(IntPtr self, out long result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseRpcDelegate(IntPtr self);

    private static IntPtr GetVtableSlot(IntPtr instance, int slot)
    {
        return Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
    }

    private static T GetDelegate<T>(IntPtr instance, int slot) where T : class
    {
        return Marshal.GetDelegateForFunctionPointer<T>(GetVtableSlot(instance, slot));
    }

    private static string Hr(int value)
    {
        return "0x" + value.ToString("X8");
    }

    private static string HstringToStringAndDelete(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            int length;
            IntPtr buffer = WindowsGetStringRawBuffer(hstring, out length);
            return buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(buffer, length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static string Json(string value)
    {
        if (value == null)
        {
            return "null";
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static string JsonInt(string hr, int value)
    {
        return "{\"hr\":" + Json(hr) + ",\"value\":" + value + "}";
    }

    private static string JsonBool(string hr, byte value)
    {
        return "{\"hr\":" + Json(hr) + ",\"value\":" + (value == 0 ? "false" : "true") + "}";
    }

    private static string Probe()
    {
        IntPtr className = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        IntPtr objectInstance = IntPtr.Zero;
        IntPtr dax = IntPtr.Zero;
        IntPtr userName = IntPtr.Zero;

        int initRo = RoInitialize(1); // RO_INIT_MULTITHREADED; RPC_E_CHANGED_MODE is acceptable here.
        try
        {
            int hr = WindowsCreateString("DAX.CDolbyDAX", 13, out className);
            if (hr < 0) throw new COMException("WindowsCreateString(class)", hr);

            Guid factoryIid = ActivationFactoryIid;
            hr = RoGetActivationFactory(className, ref factoryIid, out factory);
            if (hr < 0) throw new COMException("RoGetActivationFactory", hr);

            IntPtr activated;
            hr = GetDelegate<ActivateInstanceDelegate>(factory, 6)(factory, out activated);
            if (hr < 0) throw new COMException("IActivationFactory.ActivateInstance", hr);
            objectInstance = activated;

            Guid daxIid = DolbyDaxIid;
            hr = GetDelegate<QueryInterfaceDelegate>(objectInstance, 0)(objectInstance, ref daxIid, out dax);
            if (hr < 0) throw new COMException("QueryInterface(IDolbyDAX)", hr);

            long rpcEndpoint;
            int openRpcHr = GetDelegate<OpenRpcDelegate>(dax, 6)(dax, out rpcEndpoint);

            int v1, v2, v3, v4;
            int versionHr = GetDelegate<GetVersionDelegate>(dax, 8)(dax, out v1, out v2, out v3, out v4);

            string currentUser = Environment.UserName ?? string.Empty;
            hr = WindowsCreateString(currentUser, currentUser.Length, out userName);
            if (hr < 0) throw new COMException("WindowsCreateString(user)", hr);

            byte initResult;
            int initializeHr = GetDelegate<InitializeDelegate>(dax, 10)(dax, userName, out initResult);

            int activeProfile;
            int activeSubProfile;
            byte autoSwitch;
            int profileHr = GetDelegate<GetIntDelegate>(dax, 14)(dax, out activeProfile);
            int subProfileHr = GetDelegate<GetIntDelegate>(dax, 45)(dax, out activeSubProfile);
            int autoSwitchHr = GetDelegate<GetBoolDelegate>(dax, 59)(dax, out autoSwitch);

            IntPtr skuHstring;
            IntPtr themeHstring;
            int skuHr = GetDelegate<GetStringDelegate>(dax, 44)(dax, out skuHstring);
            int themeHr = GetDelegate<GetStringDelegate>(dax, 43)(dax, out themeHstring);

            string sku = skuHr < 0 ? null : HstringToStringAndDelete(skuHstring);
            string theme = themeHr < 0 ? null : HstringToStringAndDelete(themeHstring);

            return "{" +
                "\"roInitialize\":" + Json(Hr(initRo)) + "," +
                "\"openRpc\":{\"hr\":" + Json(Hr(openRpcHr)) + ",\"value\":" + rpcEndpoint + "}," +
                "\"version\":{\"hr\":" + Json(Hr(versionHr)) + ",\"value\":" + Json(v1 + "." + v2 + "." + v3 + "." + v4) + "}," +
                "\"initialize\":{\"hr\":" + Json(Hr(initializeHr)) + ",\"value\":" + (initResult == 0 ? "false" : "true") + "}," +
                "\"activeProfile\":" + JsonInt(Hr(profileHr), activeProfile) + "," +
                "\"activeSubProfile\":" + JsonInt(Hr(subProfileHr), activeSubProfile) + "," +
                "\"autoSwitch\":" + JsonBool(Hr(autoSwitchHr), autoSwitch) + "," +
                "\"sku\":{\"hr\":" + Json(Hr(skuHr)) + ",\"value\":" + Json(sku) + "}," +
                "\"defaultUiTheme\":{\"hr\":" + Json(Hr(themeHr)) + ",\"value\":" + Json(theme) + "}" +
                "}";
        }
        finally
        {
            // The probe is read-only with respect to profiles. Closing the RPC endpoint
            // only releases the client connection opened by this process.
            if (dax != IntPtr.Zero)
            {
                try { GetDelegate<CloseRpcDelegate>(dax, 7)(dax); } catch { }
            }
            if (userName != IntPtr.Zero) WindowsDeleteString(userName);
            if (dax != IntPtr.Zero) Marshal.Release(dax);
            if (objectInstance != IntPtr.Zero) Marshal.Release(objectInstance);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
            if (initRo == 0) RoUninitialize();
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: DaxProbe.exe <output-json>");
            return 2;
        }

        try
        {
            File.WriteAllText(args[0], Probe(), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            string error = "{\"errorType\":" + Json(ex.GetType().FullName) + ",\"message\":" + Json(ex.Message) + ",\"hresult\":" + Json(Hr(ex.HResult)) + "}";
            File.WriteAllText(args[0], error, Encoding.UTF8);
            return 1;
        }
    }
}
