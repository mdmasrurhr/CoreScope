using System.Collections.Generic;
using System.Threading.Tasks;

namespace CoreScope.Core.Control;

public sealed record RadioInfo(string Kind, string Name, bool IsOn);

/// <summary>Wi-Fi / Bluetooth radios. Implemented with the WinRT Windows.Devices.Radios API in the app project.</summary>
public interface IRadioService
{
    Task<List<RadioInfo>> GetAsync();
    Task<bool> SetAsync(string kind, bool on);
}
