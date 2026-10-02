namespace FgoPet.Infrastructure.FileSystem;

public interface IAtomicDirectoryMover
{
    /// <summary>Moves a completed directory onto a destination that must not exist.</summary>
    void Move(string source, string destination);
}

public sealed class AtomicDirectoryMover : IAtomicDirectoryMover
{
    public void Move(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"源目录不存在: {source}");
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"目标已存在: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
    }
}