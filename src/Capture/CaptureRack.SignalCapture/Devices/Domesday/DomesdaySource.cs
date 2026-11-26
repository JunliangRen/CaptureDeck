namespace CaptureRack.SignalCapture;

internal sealed record DomesdaySource(string UsbId, string InstanceId, string Protocol, string? FirmwareRelease,
    string? Fx3Commit, string? GatewareCommit, int SampleRateHz, int Decimation, bool TestMode, string? Sw401, string Format, int SoftwareDecimation = 1)
{
    public int HardwareSampleRateHz => SampleRateHz * SoftwareDecimation;
    public string Device => "Domesday Duplicator";
    public IEnumerable<string> FlacTags()
    {
        yield return "RF_SOURCE=Domesday Duplicator";
        yield return $"DDD_SAMPLE_RATE_HZ={SampleRateHz}";
        yield return $"DDD_HARDWARE_SAMPLE_RATE_HZ={HardwareSampleRateHz}";
        yield return $"DDD_SOFTWARE_DECIMATION={SoftwareDecimation}";
        yield return $"DDD_DECIMATION={Decimation}";
        yield return TestMode ? "DDD_TEST_MODE=true" : "DDD_TEST_MODE=false";
        if (Fx3Commit is not null) yield return $"DDD_FIRMWARE_VERSION={Fx3Commit}";
        if (GatewareCommit is not null) yield return $"DDD_GATEWARE_VERSION={GatewareCommit}";
        if (FirmwareRelease is not null) yield return $"DDD_FIRMWARE_RELEASE={FirmwareRelease}";
        if (Sw401 is not null) yield return $"DDD_FRONT_END_GAIN=SW401 {Sw401}";
    }
}
