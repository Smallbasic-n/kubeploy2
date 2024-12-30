namespace kubeploy2.Manifest;

public class Binding
{
    public string Name { get; set; } = null!;
    public string Protocol { get; set; } = null!;
    public string Scheme { get; set; } = null!;
    public string Transport { get; set; } = null!;
    public int TargetPort { get; set; }
    public int Port
    {
        get=> TargetPort; set => TargetPort = value;
    }
}