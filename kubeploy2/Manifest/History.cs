namespace kubeploy2.Manifest;

public class History
{
    public DateTime Date { get; set; }
    public int MajorVersion { get; set; }
    public int MinorVersion { get; set; }
    public int Patch { get; set; }
    public PreviewType Preview { get; set; }
}

public enum PreviewType
{
    None,
    Alpha,
    Beta
}