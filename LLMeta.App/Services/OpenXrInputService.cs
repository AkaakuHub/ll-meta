using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed class OpenXrInputService
{
    public unsafe OpenXrSessionProbeResult ProbeHeadMountedDisplaySession()
    {
        var xr = XR.GetApi();
        var extensionSupport = ProbeInstanceExtensionSupport(xr);
        var applicationInfo = CreateApplicationInfo();
        var enabledExtensions = extensionSupport.SupportsKhrD3D11Enable
            ? new[] { "XR_KHR_D3D11_enable" }
            : Array.Empty<string>();
        var enabledExtensionsPointer =
            enabledExtensions.Length == 0
                ? (byte**)0
                : (byte**)
                    SilkMarshal.StringArrayToPtr(enabledExtensions, NativeStringEncoding.UTF8);
        var instanceCreateInfo = new InstanceCreateInfo
        {
            Type = StructureType.InstanceCreateInfo,
            ApplicationInfo = applicationInfo,
            EnabledExtensionCount = (uint)enabledExtensions.Length,
            EnabledExtensionNames = enabledExtensionsPointer,
        };

        var instance = new Instance();
        var instanceCreateResult = xr.CreateInstance(ref instanceCreateInfo, ref instance);
        if (instanceCreateResult != Result.Success)
        {
            if ((nint)enabledExtensionsPointer != 0)
            {
                SilkMarshal.Free((nint)enabledExtensionsPointer);
            }

            return new OpenXrSessionProbeResult(
                extensionSupport.EnumerateResult,
                extensionSupport.SupportsKhrD3D11Enable,
                extensionSupport.SupportsKhrD3D12Enable,
                extensionSupport.SupportsMndHeadless,
                instanceCreateResult,
                instanceCreateResult,
                instanceCreateResult,
                XR.NullSystemID,
                0,
                instanceCreateResult,
                null
            );
        }

        ulong systemId = XR.NullSystemID;
        var getSystemResult = Result.Success;
        var getD3D11GraphicsRequirementsResult = Result.Success;
        var d3d11CreateDeviceResult = 0;
        var sessionCreateResult = Result.Success;
        string? diagnostics = null;
        ID3D11Device* d3d11Device = null;
        ID3D11DeviceContext* d3d11DeviceContext = null;
        try
        {
            var systemGetInfo = new SystemGetInfo
            {
                Type = StructureType.SystemGetInfo,
                FormFactor = FormFactor.HeadMountedDisplay,
            };
            getSystemResult = xr.GetSystem(instance, ref systemGetInfo, ref systemId);
            if (getSystemResult != Result.Success)
            {
                return new OpenXrSessionProbeResult(
                    extensionSupport.EnumerateResult,
                    extensionSupport.SupportsKhrD3D11Enable,
                    extensionSupport.SupportsKhrD3D12Enable,
                    extensionSupport.SupportsMndHeadless,
                    instanceCreateResult,
                    getSystemResult,
                    getSystemResult,
                    systemId,
                    d3d11CreateDeviceResult,
                    getSystemResult,
                    diagnostics
                );
            }

            if (!extensionSupport.SupportsKhrD3D11Enable)
            {
                diagnostics = "XR_KHR_D3D11_enable is not supported by active runtime.";
                return new OpenXrSessionProbeResult(
                    extensionSupport.EnumerateResult,
                    extensionSupport.SupportsKhrD3D11Enable,
                    extensionSupport.SupportsKhrD3D12Enable,
                    extensionSupport.SupportsMndHeadless,
                    instanceCreateResult,
                    getSystemResult,
                    Result.ErrorExtensionNotPresent,
                    systemId,
                    d3d11CreateDeviceResult,
                    Result.ErrorExtensionNotPresent,
                    diagnostics
                );
            }

            var d3d11GraphicsRequirements = new GraphicsRequirementsD3D11KHR
            {
                Type = StructureType.GraphicsRequirementsD3D11Khr,
            };
            PfnVoidFunction d3d11GraphicsRequirementsProc = default;
            var getProcAddrResult = xr.GetInstanceProcAddr(
                instance,
                "xrGetD3D11GraphicsRequirementsKHR",
                ref d3d11GraphicsRequirementsProc
            );
            if (getProcAddrResult != Result.Success)
            {
                diagnostics = $"xrGetInstanceProcAddr failed: {getProcAddrResult}";
                return new OpenXrSessionProbeResult(
                    extensionSupport.EnumerateResult,
                    extensionSupport.SupportsKhrD3D11Enable,
                    extensionSupport.SupportsKhrD3D12Enable,
                    extensionSupport.SupportsMndHeadless,
                    instanceCreateResult,
                    getSystemResult,
                    getProcAddrResult,
                    systemId,
                    d3d11CreateDeviceResult,
                    getProcAddrResult,
                    diagnostics
                );
            }

            var d3d11GraphicsRequirementsProcPointer = (nint)d3d11GraphicsRequirementsProc;
            if (d3d11GraphicsRequirementsProcPointer == 0)
            {
                diagnostics = "xrGetD3D11GraphicsRequirementsKHR pointer is null.";
                return new OpenXrSessionProbeResult(
                    extensionSupport.EnumerateResult,
                    extensionSupport.SupportsKhrD3D11Enable,
                    extensionSupport.SupportsKhrD3D12Enable,
                    extensionSupport.SupportsMndHeadless,
                    instanceCreateResult,
                    getSystemResult,
                    Result.ErrorFunctionUnsupported,
                    systemId,
                    d3d11CreateDeviceResult,
                    Result.ErrorFunctionUnsupported,
                    diagnostics
                );
            }

            try
            {
                var getD3D11GraphicsRequirements = (delegate* unmanaged[Stdcall]<
                    Instance,
                    ulong,
                    GraphicsRequirementsD3D11KHR*,
                    Result>)d3d11GraphicsRequirementsProcPointer;
                getD3D11GraphicsRequirementsResult = getD3D11GraphicsRequirements(
                    instance,
                    systemId,
                    &d3d11GraphicsRequirements
                );
                if (getD3D11GraphicsRequirementsResult != Result.Success)
                {
                    return new OpenXrSessionProbeResult(
                        extensionSupport.EnumerateResult,
                        extensionSupport.SupportsKhrD3D11Enable,
                        extensionSupport.SupportsKhrD3D12Enable,
                        extensionSupport.SupportsMndHeadless,
                        instanceCreateResult,
                        getSystemResult,
                        getD3D11GraphicsRequirementsResult,
                        systemId,
                        d3d11CreateDeviceResult,
                        getD3D11GraphicsRequirementsResult,
                        diagnostics
                    );
                }
            }
            catch (Exception ex)
            {
                diagnostics = ex.Message;
                return new OpenXrSessionProbeResult(
                    extensionSupport.EnumerateResult,
                    extensionSupport.SupportsKhrD3D11Enable,
                    extensionSupport.SupportsKhrD3D12Enable,
                    extensionSupport.SupportsMndHeadless,
                    instanceCreateResult,
                    getSystemResult,
                    Result.ErrorFunctionUnsupported,
                    systemId,
                    d3d11CreateDeviceResult,
                    Result.ErrorFunctionUnsupported,
                    diagnostics
                );
            }

            var d3d11 = D3D11.GetApi();
            var minimumFeatureLevel = (D3DFeatureLevel)d3d11GraphicsRequirements.MinFeatureLevel;
            var featureLevels = stackalloc D3DFeatureLevel[] { minimumFeatureLevel };
            D3DFeatureLevel d3dFeatureLevel;
            d3d11CreateDeviceResult = d3d11.CreateDevice(
                (IDXGIAdapter*)0,
                D3DDriverType.Hardware,
                IntPtr.Zero,
                0,
                featureLevels,
                1,
                (uint)D3D11.SdkVersion,
                &d3d11Device,
                &d3dFeatureLevel,
                &d3d11DeviceContext
            );
            if (d3d11CreateDeviceResult < 0)
            {
                d3d11CreateDeviceResult = d3d11.CreateDevice(
                    (IDXGIAdapter*)0,
                    D3DDriverType.Warp,
                    IntPtr.Zero,
                    0,
                    featureLevels,
                    1,
                    (uint)D3D11.SdkVersion,
                    &d3d11Device,
                    &d3dFeatureLevel,
                    &d3d11DeviceContext
                );
            }

            if (d3d11CreateDeviceResult < 0)
            {
                return new OpenXrSessionProbeResult(
                    extensionSupport.EnumerateResult,
                    extensionSupport.SupportsKhrD3D11Enable,
                    extensionSupport.SupportsKhrD3D12Enable,
                    extensionSupport.SupportsMndHeadless,
                    instanceCreateResult,
                    getSystemResult,
                    getD3D11GraphicsRequirementsResult,
                    systemId,
                    d3d11CreateDeviceResult,
                    Result.ErrorGraphicsDeviceInvalid,
                    diagnostics
                );
            }

            var graphicsBindingD3D11 = new GraphicsBindingD3D11KHR
            {
                Type = StructureType.GraphicsBindingD3D11Khr,
                Device = d3d11Device,
            };
            var sessionCreateInfo = new SessionCreateInfo
            {
                Type = StructureType.SessionCreateInfo,
                Next = &graphicsBindingD3D11,
                SystemId = systemId,
            };
            var session = new Session();
            sessionCreateResult = xr.CreateSession(instance, ref sessionCreateInfo, ref session);
            if (sessionCreateResult == Result.Success)
            {
                xr.DestroySession(session);
            }

            return new OpenXrSessionProbeResult(
                extensionSupport.EnumerateResult,
                extensionSupport.SupportsKhrD3D11Enable,
                extensionSupport.SupportsKhrD3D12Enable,
                extensionSupport.SupportsMndHeadless,
                instanceCreateResult,
                getSystemResult,
                getD3D11GraphicsRequirementsResult,
                systemId,
                d3d11CreateDeviceResult,
                sessionCreateResult,
                diagnostics
            );
        }
        finally
        {
            if (d3d11DeviceContext is not null)
            {
                _ = d3d11DeviceContext->Release();
            }

            if (d3d11Device is not null)
            {
                _ = d3d11Device->Release();
            }

            if (instance.Handle != 0)
            {
                xr.DestroyInstance(instance);
            }

            if ((nint)enabledExtensionsPointer != 0)
            {
                SilkMarshal.Free((nint)enabledExtensionsPointer);
            }
        }
    }

    private static unsafe OpenXrExtensionSupport ProbeInstanceExtensionSupport(XR xr)
    {
        uint extensionCount = 0;
        var enumerateResult = xr.EnumerateInstanceExtensionProperties(
            (byte*)0,
            0,
            ref extensionCount,
            (ExtensionProperties*)0
        );
        if (enumerateResult != Result.Success)
        {
            return new OpenXrExtensionSupport(enumerateResult, false, false, false);
        }

        var properties = new ExtensionProperties[extensionCount];
        for (var i = 0; i < properties.Length; i++)
        {
            properties[i].Type = StructureType.ExtensionProperties;
        }

        fixed (ExtensionProperties* propertiesPointer = properties)
        {
            enumerateResult = xr.EnumerateInstanceExtensionProperties(
                (byte*)0,
                extensionCount,
                ref extensionCount,
                propertiesPointer
            );
        }

        if (enumerateResult != Result.Success)
        {
            return new OpenXrExtensionSupport(enumerateResult, false, false, false);
        }

        var supportsKhrD3D11Enable = false;
        var supportsKhrD3D12Enable = false;
        var supportsMndHeadless = false;
        for (var i = 0; i < properties.Length; i++)
        {
            fixed (byte* extensionNamePointer = properties[i].ExtensionName)
            {
                var extensionName = SilkMarshal.PtrToString(
                    (nint)extensionNamePointer,
                    NativeStringEncoding.UTF8
                );
                if (extensionName == "XR_KHR_D3D11_enable")
                {
                    supportsKhrD3D11Enable = true;
                }
                else if (extensionName == "XR_KHR_D3D12_enable")
                {
                    supportsKhrD3D12Enable = true;
                }
                else if (extensionName == "XR_MND_headless")
                {
                    supportsMndHeadless = true;
                }
            }
        }

        return new OpenXrExtensionSupport(
            enumerateResult,
            supportsKhrD3D11Enable,
            supportsKhrD3D12Enable,
            supportsMndHeadless
        );
    }

    private static unsafe ApplicationInfo CreateApplicationInfo()
    {
        var applicationInfo = new ApplicationInfo
        {
            ApplicationVersion = 1,
            EngineVersion = 1,
            ApiVersion = (ulong)new Version64(1, 0, 0),
        };

        var appName = applicationInfo.ApplicationName;
        var appNameSpan = new Span<byte>(appName, (int)XR.MaxApplicationNameSize);
        _ = SilkMarshal.StringIntoSpan("LLMeta.App", appNameSpan, NativeStringEncoding.UTF8);

        var engineName = applicationInfo.EngineName;
        var engineNameSpan = new Span<byte>(engineName, (int)XR.MaxEngineNameSize);
        _ = SilkMarshal.StringIntoSpan("LLMeta.OpenXR", engineNameSpan, NativeStringEncoding.UTF8);

        return applicationInfo;
    }
}

public readonly record struct OpenXrSessionProbeResult(
    Result EnumerateExtensionsResult,
    bool SupportsKhrD3D11Enable,
    bool SupportsKhrD3D12Enable,
    bool SupportsMndHeadless,
    Result InstanceCreateResult,
    Result GetSystemResult,
    Result GetD3D11GraphicsRequirementsResult,
    ulong SystemId,
    int D3D11CreateDeviceHResult,
    Result CreateSessionResult,
    string? Diagnostics
);

public readonly record struct OpenXrExtensionSupport(
    Result EnumerateResult,
    bool SupportsKhrD3D11Enable,
    bool SupportsKhrD3D12Enable,
    bool SupportsMndHeadless
);
