using Aspire.Hosting.ApplicationModel;

namespace F23.Aspire.Hosting.AzureSearchEmulator;

public class AzureSearchEmulatorResource(string name) : ContainerResource(name)
{
    public const int DefaultHttpPort = 5100;
    public const int DefaultHttpsPort = 5143;
}
