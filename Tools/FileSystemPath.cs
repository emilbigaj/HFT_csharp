
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
                // Why not a Linux check: Android is not Linux to .NET, and every non-Windows system writes paths the Linux way
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSType.Windows : OSType.Linux;
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

        /// <summary>
        /// Path join, mirroring std::filesystem::path::operator/ on the C++ side. Use this — never
        /// string concatenation — to build shared-memory region names: Tools.Memory sanitizes every
        /// character outside [A-Za-z0-9_\-.] to '_', so the separator this inserts becomes the same
        /// '_' the C++ join produces. Concatenating instead names a different, empty region, and
        /// CreateOrOpen happily creates it — the symptom is a hang or a permanently empty read, not
        /// an error.
        /// </summary>
        public static FileSystemPath operator /(FileSystemPath left, string right)
        {
            return new FileSystemPath(System.IO.Path.Combine(left.Path, right));
        }

        public static FileSystemPath operator /(FileSystemPath left, FileSystemPath right)
        {
            return new FileSystemPath(System.IO.Path.Combine(left.Path, right.Path));
        }

        public static bool operator ==(FileSystemPath left, FileSystemPath right)
        {
            return left.Path == right.Path;
        }

        public static bool operator !=(FileSystemPath left, FileSystemPath right)
        {
            return left.Path != right.Path;
        }

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
