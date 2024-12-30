namespace kubeploy2.Manifest;

public class Storage
{
    public string MountPath { get; set; } = null!;
    public string PvName { get; set; } = null!;
    public bool ReadOnly { get; set; } = false;
    public string PvSize{ get; set; } = null!;
    public Access Access{ get; set; }
}

public enum Access
{
    ReadWriteMany,
    ReadWriteOnce
}