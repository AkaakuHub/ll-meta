using LLMeta.App.Models;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private static Result StringToPath(Instance instance, string pathString, out ulong path)
    {
        path = XR.NullPath;
        var xr = XR.GetApi();
        return xr.StringToPath(instance, pathString, ref path);
    }

    private static OpenXrExtensionSupport ProbeInstanceExtensionSupport(XR xr)
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
            return new OpenXrExtensionSupport(enumerateResult, false);
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
            return new OpenXrExtensionSupport(enumerateResult, false);
        }

        var supportsKhrD3D11Enable = false;
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
            }
        }

        return new OpenXrExtensionSupport(enumerateResult, supportsKhrD3D11Enable);
    }

    private static ApplicationInfo CreateApplicationInfo()
    {
        var applicationInfo = new ApplicationInfo
        {
            ApplicationVersion = 1,
            EngineVersion = 1,
            ApiVersion = (ulong)new Version64(1, 0, 0),
        };

        var applicationName = applicationInfo.ApplicationName;
        WriteFixedUtf8(applicationName, (int)XR.MaxApplicationNameSize, "LLMeta.App");
        var engineName = applicationInfo.EngineName;
        WriteFixedUtf8(engineName, (int)XR.MaxEngineNameSize, "LLMeta.OpenXR");
        return applicationInfo;
    }

    private static void WriteFixedUtf8(byte* fixedBuffer, int bufferLength, string value)
    {
        var span = new Span<byte>(fixedBuffer, bufferLength);
        span.Clear();
        _ = SilkMarshal.StringIntoSpan(value, span, NativeStringEncoding.UTF8);
    }
}
