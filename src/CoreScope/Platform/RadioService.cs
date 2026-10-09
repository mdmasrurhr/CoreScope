using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CoreScope.Core;
using CoreScope.Core.Control;
using Windows.Devices.Radios;

namespace CoreScope.Platform;

/// <summary>Wi-Fi and Bluetooth radio switches via Windows.Devices.Radios (the API behind the Action Center toggles).</summary>
public sealed class RadioService : IRadioService
{
    private static string KindName(RadioKind kind) => kind switch
    {
        RadioKind.WiFi => "Wi-Fi",
        RadioKind.Bluetooth => "Bluetooth",
        RadioKind.MobileBroadband => "Mobile broadband",
        _ => "",
    };

    private static async Task<IReadOnlyList<Radio>> RadiosAsync()
    {
        var access = await Radio.RequestAccessAsync();
        if (access != RadioAccessStatus.Allowed)
        {
            Log.Info($"Radios: access {access}");
            return Array.Empty<Radio>();
        }
        return await Radio.GetRadiosAsync();
    }

    public async Task<List<RadioInfo>> GetAsync()
    {
        var radios = await RadiosAsync();
        return radios
            .Where(r => KindName(r.Kind).Length > 0)
            .GroupBy(r => r.Kind)
            .Select(g => g.First())
            .Select(r => new RadioInfo(KindName(r.Kind), r.Name, r.State == RadioState.On))
            .ToList();
    }

    public async Task<bool> SetAsync(string kind, bool on)
    {
        var radio = (await RadiosAsync()).FirstOrDefault(r => KindName(r.Kind) == kind);
        if (radio is null) return false;
        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        Log.Info($"Control: {kind} radio → {(on ? "on" : "off")} : {result}");
        return result == RadioAccessStatus.Allowed;
    }
}
