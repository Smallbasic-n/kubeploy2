namespace kubeploy2.Manifest;

public class Manifest
{
    public string? ProjectName { get; set; } = null;
    public string ServiceName { get; set; } = null!;
    public string KubernetesName { get; set; } = null!;
    public string CustomImage { get; set; } = null!;
    public Storage[] Storages { get; set; } = null!;
    public EnvironmentVar[] EnvironmentVars { get; set; } = null!;
    public Binding[] Bindings { get; set; } = null!;
    public History[] History { get; set; } = null!;
}