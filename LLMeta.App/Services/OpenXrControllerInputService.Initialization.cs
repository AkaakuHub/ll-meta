using LLMeta.App.Models;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    public OpenXrControllerState Initialize()
    {
        if (_isInitialized)
        {
            return CreateState("Initialized");
        }

        _xr = XR.GetApi();
        var extensionSupport = ProbeInstanceExtensionSupport(_xr);
        if (extensionSupport.EnumerateResult != Result.Success)
        {
            return CreateState($"Enumerate extensions failed: {extensionSupport.EnumerateResult}");
        }

        if (!extensionSupport.SupportsKhrD3D11Enable)
        {
            return CreateState("XR_KHR_D3D11_enable is not supported.");
        }

        var applicationInfo = CreateApplicationInfo();
        var enabledExtensions = new[] { "XR_KHR_D3D11_enable" };
        var enabledExtensionsPointer = (byte**)
            SilkMarshal.StringArrayToPtr(enabledExtensions, NativeStringEncoding.UTF8);

        try
        {
            var instanceCreateInfo = new InstanceCreateInfo
            {
                Type = StructureType.InstanceCreateInfo,
                ApplicationInfo = applicationInfo,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                EnabledExtensionNames = enabledExtensionsPointer,
            };
            var createInstanceResult = _xr.CreateInstance(ref instanceCreateInfo, ref _instance);
            if (createInstanceResult != Result.Success)
            {
                return CreateState($"CreateInstance failed: {createInstanceResult}");
            }

            var systemGetInfo = new SystemGetInfo
            {
                Type = StructureType.SystemGetInfo,
                FormFactor = FormFactor.HeadMountedDisplay,
            };
            ulong systemId = XR.NullSystemID;
            var getSystemResult = _xr.GetSystem(_instance, ref systemGetInfo, ref systemId);
            if (getSystemResult != Result.Success)
            {
                return CreateState($"GetSystem failed: {getSystemResult}");
            }
            _systemId = systemId;

            var getRequirementsResult = GetD3D11GraphicsRequirements(_instance, systemId);
            if (getRequirementsResult != Result.Success)
            {
                return CreateState($"GetD3D11GraphicsRequirements failed: {getRequirementsResult}");
            }

            var d3d11CreateResult = CreateD3D11Device();
            if (d3d11CreateResult != 0)
            {
                return CreateState($"D3D11 create failed: 0x{d3d11CreateResult:X8}");
            }

            var graphicsBinding = new GraphicsBindingD3D11KHR
            {
                Type = StructureType.GraphicsBindingD3D11Khr,
                Device = _d3d11Device,
            };
            var sessionCreateInfo = new SessionCreateInfo
            {
                Type = StructureType.SessionCreateInfo,
                Next = &graphicsBinding,
                SystemId = systemId,
            };
            var createSessionResult = _xr.CreateSession(
                _instance,
                ref sessionCreateInfo,
                ref _session
            );
            if (createSessionResult != Result.Success)
            {
                return CreateState($"CreateSession failed: {createSessionResult}");
            }

            var initializeActionsResult = InitializeActions();
            if (initializeActionsResult != Result.Success)
            {
                return CreateState($"InitializeActions failed: {initializeActionsResult}");
            }

            var initializeHeadTrackingResult = InitializeHeadTrackingSpaces();
            if (initializeHeadTrackingResult != Result.Success)
            {
                return CreateState(
                    $"InitializeHeadTrackingSpaces failed: {initializeHeadTrackingResult}"
                );
            }

            var initializeStereoResult = InitializeStereoRendering();
            if (initializeStereoResult != Result.Success)
            {
                return CreateState($"InitializeStereoRendering failed: {initializeStereoResult}");
            }

            _isInitialized = true;
            if (_bindingSupportSummary.Length > 0)
            {
                return CreateState($"Initialized | {_bindingSupportSummary}");
            }

            return CreateState("Initialized");
        }
        catch (Exception ex)
        {
            return CreateState($"Initialize exception: {ex.Message}");
        }
        finally
        {
            SilkMarshal.Free((nint)enabledExtensionsPointer);
        }
    }
}
