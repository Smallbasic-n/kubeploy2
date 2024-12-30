namespace kubeploy2.Manifest;

public class Setting
{
    public string Prefix { get; set; } = null!;
    public string Kubeconfig { get; set; } = null!;
    public string Context {get; set;} = null!;
    public string Registry { get; set; } = null!;
    public string ApplicationName { get; set; } = null!;
    public string ImagePullSecret { get; set; } = null!;
    public string Namespace { get; set; } = null!;
    public string StorageClass { get; set; } = null!;
    public string AppHostProject { get; set; } = null!;
}