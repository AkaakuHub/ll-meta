using System;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private Result GetD3D11GraphicsRequirements(Instance instance, ulong systemId)
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        PfnVoidFunction getRequirementsProc = default;
        var procResult = _xr.GetInstanceProcAddr(
            instance,
            "xrGetD3D11GraphicsRequirementsKHR",
            ref getRequirementsProc
        );
        if (procResult != Result.Success)
        {
            return procResult;
        }

        var procPointer = (nint)getRequirementsProc;
        if (procPointer == 0)
        {
            return Result.ErrorFunctionUnsupported;
        }

        var graphicsRequirements = new GraphicsRequirementsD3D11KHR
        {
            Type = StructureType.GraphicsRequirementsD3D11Khr,
        };
        var getRequirements = (delegate* unmanaged[Stdcall]<
            Instance,
            ulong,
            GraphicsRequirementsD3D11KHR*,
            Result>)procPointer;
        var result = getRequirements(instance, systemId, &graphicsRequirements);
        if (result == Result.Success)
        {
            _requiredGraphicsAdapterLuid = graphicsRequirements.AdapterLuid;
            _hasRequiredGraphicsAdapterLuid = true;
            _graphicsAdapterSummary = $"requiredLuid=0x{graphicsRequirements.AdapterLuid:X16}";
        }

        return result;
    }

    private int CreateD3D11Device()
    {
        if (!_requestedGraphicsBackendLabel.Equals("D3D11", StringComparison.OrdinalIgnoreCase))
        {
            _graphicsAdapterSummary =
                $"backend-not-supported requested={_requestedGraphicsBackendLabel}";
            return unchecked((int)0x80070057);
        }

        lock (_videoFrameLock)
        {
            _availableGraphicsBackends = ["D3D11"];
            _selectedGraphicsBackendLabel = "D3D11";
        }

        var d3d11 = D3D11.GetApi();
        var dxgi = DXGI.GetApi();
        D3DFeatureLevel featureLevel;
        ID3D11Device* d3d11Device = null;
        ID3D11DeviceContext* d3d11DeviceContext = null;
        IDXGIFactory1* dxgiFactory = null;
        IDXGIAdapter1* selectedAdapter = null;

        if (!_hasRequiredGraphicsAdapterLuid)
        {
            return unchecked((int)0x887A0002);
        }

        try
        {
            void* factoryPointer = null;
            var factoryGuid = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
            var createFactoryResult = dxgi.CreateDXGIFactory1(ref factoryGuid, ref factoryPointer);
            if (createFactoryResult < 0 || factoryPointer is null)
            {
                return createFactoryResult;
            }

            dxgiFactory = (IDXGIFactory1*)factoryPointer;
            var requestedAdapterLuid = ParsePreferredAdapterLuidOrDefault(
                _requestedGraphicsAdapterLabel,
                _requiredGraphicsAdapterLuid
            );
            lock (_videoFrameLock)
            {
                _availableGraphicsAdapterLabels = ["Auto"];
            }
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                IDXGIAdapter1* adapter = null;
                var enumResult = dxgiFactory->EnumAdapters1(adapterIndex, &adapter);
                if (enumResult == DxgiErrorNotFound)
                {
                    break;
                }

                if (enumResult < 0 || adapter is null)
                {
                    break;
                }

                AdapterDesc1 adapterDesc = default;
                if (adapter->GetDesc1(&adapterDesc) >= 0)
                {
                    var adapterLuid = ConvertLuidToUInt64(adapterDesc.AdapterLuid);
                    var adapterLabel = BuildAdapterLabel(adapterDesc, adapterLuid, adapterIndex);
                    lock (_videoFrameLock)
                    {
                        _availableGraphicsAdapterLabels.Add(adapterLabel);
                    }

                    if (adapterLuid == requestedAdapterLuid)
                    {
                        selectedAdapter = adapter;
                        _graphicsAdapterSummary =
                            $"requiredLuid=0x{_requiredGraphicsAdapterLuid:X16} selectedAdapterIndex={adapterIndex} requestedLuid=0x{requestedAdapterLuid:X16}";
                        if (requestedAdapterLuid != _requiredGraphicsAdapterLuid)
                        {
                            _selectedGraphicsAdapterLabel = adapterLabel;
                            _graphicsAdapterSummary =
                                $"{_graphicsAdapterSummary} incompatible-with-openxr-required-adapter";
                            return unchecked((int)0x80070057);
                        }
                        _selectedGraphicsAdapterLabel = adapterLabel;
                        break;
                    }
                }

                _ = adapter->Release();
            }
        }
        finally
        {
            if (dxgiFactory is not null)
            {
                _ = dxgiFactory->Release();
            }
        }

        if (selectedAdapter is null)
        {
            _graphicsAdapterSummary =
                $"adapter-not-found requested={_requestedGraphicsAdapterLabel} requiredLuid=0x{_requiredGraphicsAdapterLuid:X16}";
            return DxgiErrorNotFound;
        }

        var createResult = d3d11.CreateDevice(
            (IDXGIAdapter*)selectedAdapter,
            D3DDriverType.Unknown,
            IntPtr.Zero,
            (uint)(CreateDeviceFlag.BgraSupport | CreateDeviceFlag.VideoSupport),
            (D3DFeatureLevel*)0,
            0,
            (uint)D3D11.SdkVersion,
            &d3d11Device,
            &featureLevel,
            &d3d11DeviceContext
        );
        _ = selectedAdapter->Release();
        if (createResult >= 0)
        {
            _d3d11Device = d3d11Device;
            _d3d11DeviceContext = d3d11DeviceContext;
            return createResult;
        }

        return createResult;
    }

    private static ulong ConvertLuidToUInt64(Luid luid)
    {
        Span<Luid> luidSpan = stackalloc Luid[1];
        luidSpan[0] = luid;
        var bytes = MemoryMarshal.AsBytes(luidSpan);
        return BitConverter.ToUInt64(bytes);
    }

    private static ulong ParsePreferredAdapterLuidOrDefault(
        string requestedAdapter,
        ulong fallbackLuid
    )
    {
        if (
            string.IsNullOrWhiteSpace(requestedAdapter)
            || requestedAdapter.Equals("Auto", StringComparison.OrdinalIgnoreCase)
        )
        {
            return fallbackLuid;
        }

        var normalized = requestedAdapter.Trim();
        var marker = "LUID=0x";
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var hexStart = markerIndex + marker.Length;
            var hexEnd = normalized.IndexOfAny([' ', '|'], hexStart);
            var hexLength = (hexEnd >= 0 ? hexEnd : normalized.Length) - hexStart;
            if (hexLength > 0)
            {
                var hexToken = normalized.Substring(hexStart, hexLength);
                if (
                    ulong.TryParse(
                        hexToken,
                        System.Globalization.NumberStyles.HexNumber,
                        null,
                        out var parsedFromLabel
                    )
                )
                {
                    return parsedFromLabel;
                }
            }
        }

        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (
            ulong.TryParse(
                normalized,
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var parsed
            )
        )
        {
            return parsed;
        }

        return fallbackLuid;
    }

    private static string BuildAdapterLabel(
        AdapterDesc1 adapterDesc,
        ulong adapterLuid,
        uint adapterIndex
    )
    {
        _ = adapterDesc;
        return $"Adapter idx={adapterIndex} | LUID=0x{adapterLuid:X16}";
    }
}
