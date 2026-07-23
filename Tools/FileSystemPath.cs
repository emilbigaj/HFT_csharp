
using System;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Tools
{
    public enum OSType
    {
        Windows,
        Linux,
    }

    public class FileSystemPath
    {
        public string Path { get; }
        public static OSType OSType
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return OSType.Windows;
                }
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return OSType.Linux;
                }
                throw new Exception("Unsupported OS platform.");
            }
        }
        public FileSystemPath(string path)
        {
            if (OSType == OSType.Windows)
            {
                const string pattern = @"^/mnt/([a-zA-Z])(?:/|$)";
                Path = Regex.Replace(path, pattern, "$1:\\").Replace('/', '\\');
            }
            else
            {
                const string pattern = @"^([a-zA-Z]):(?:\\|$)";
                Path = Regex.Replace(path, pattern, "/mnt/$1/").Replace('\\', '/');
            }
        }

        public FileSystemPath GetPathWithoutExtension()
        {
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? Path, System.IO.Path.GetFileNameWithoutExtension(Path));
        }

        public int Length => Path.Length;

        public static implicit operator FileSystemPath(string path)
        {
            return new FileSystemPath(path);
        }


        public static implicit operator string(FileSystemPath fsp)
        {
            return fsp?.Path ?? string.Empty;
        }

        public override string ToString()
        {
            return Path;
        }


    }
}
