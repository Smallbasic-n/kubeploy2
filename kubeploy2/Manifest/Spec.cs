

namespace kubeploy2.Manifest;

public class Spec
{
    public EnvironmentVar[] UserInputValue { get; set; } = null!;
    public Manifest[] Manifests { get; set; } = null!;
}