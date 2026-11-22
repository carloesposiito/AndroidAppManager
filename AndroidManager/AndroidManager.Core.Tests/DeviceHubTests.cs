using AndroidManager.Core.Adb;
using AndroidManager.Core.Devices;

namespace AndroidManager.Core.Tests;

public class DeviceHubTests
{
    private static DeviceSession S(string serial) => new(AdbEndpoint.ForSerial(serial), serial, "p");

    [Fact]
    public void Starts_empty_and_unselected()
    {
        var hub = new DeviceHub();
        Assert.Empty(hub.Devices);
        Assert.Null(hub.Selected);
        Assert.Null(hub.ActiveEndpoint);
        Assert.False(hub.Busy);
    }

    [Fact]
    public void A_lone_device_is_selected_automatically_two_are_not()
    {
        var hub = new DeviceHub();
        hub.SetDevices([S("A")]);
        Assert.Equal("A", hub.Selected!.Endpoint.Serial);

        var two = new DeviceHub();
        two.SetDevices([S("A"), S("B")]);
        Assert.Null(two.Selected);
    }

    [Fact]
    public void Known_device_keeps_its_session_and_info_and_the_selection()
    {
        var hub = new DeviceHub();
        hub.SetDevices([S("A"), S("B")]);
        hub.Select("B");
        var b = hub.Selected!;
        b.Info = new DeviceInfo("B", "m", "mod", "13", 33, "arm64");

        hub.SetDevices([S("A"), S("B"), S("C")]);

        Assert.Same(b, hub.Selected);
        Assert.NotNull(hub.Devices.Single(d => d.Endpoint.Serial == "B").Info);
        Assert.Equal(3, hub.Devices.Count);
    }

    [Fact]
    public void Selection_clears_when_its_device_disappears()
    {
        var hub = new DeviceHub();
        hub.SetDevices([S("A"), S("B")]);
        hub.Select("A");
        hub.SetDevices([S("B"), S("C")]);
        Assert.Null(hub.Selected);
    }

    [Fact]
    public void Duplicate_serials_collapse()
    {
        var hub = new DeviceHub();
        hub.SetDevices([S("A"), S("A")]);
        Assert.Single(hub.Devices);
    }

    [Fact]
    public void Select_rejects_unknown_serial_and_null_clears()
    {
        var hub = new DeviceHub();
        hub.SetDevices([S("A"), S("B")]);
        Assert.False(hub.Select("Z"));
        Assert.Null(hub.Selected);
        Assert.True(hub.Select("A"));
        Assert.True(hub.Select(null));
        Assert.Null(hub.Selected);
    }

    [Fact]
    public void Others_excludes_the_selected_device()
    {
        var hub = new DeviceHub();
        hub.SetDevices([S("A"), S("B"), S("C")]);
        hub.Select("B");
        Assert.Equal(["A", "C"], hub.Others.Select(d => d.Endpoint.Serial));
    }

    [Fact]
    public void Events_fire_only_on_real_changes()
    {
        var hub = new DeviceHub();
        var n = 0;
        hub.Changed += () => n++;
        hub.SetBusy(false);             // already idle
        hub.SetDevices([]);             // nothing changes
        hub.Select(null);               // already none
        Assert.Equal(0, n);

        hub.SetDevices([S("A"), S("B")]);  // list changed
        hub.Select("A");                   // selection changed
        hub.Select("A");                   // same again
        hub.SetDevices([S("A"), S("B")]);  // same list
        hub.SetBusy(true);
        Assert.Equal(3, n);
        Assert.True(hub.Busy);
    }
}
