using System;
using System.IO;
using System.Reflection;

namespace ScreenRecorder
{
    // The SharpDX DLLs are embedded in the exe (logical name "lib.<name>.dll") so
    // the program stays a single file.
    internal static class EmbeddedAssemblyResolver
    {
        public static void Register()
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            using (Stream stream = typeof(EmbeddedAssemblyResolver).Assembly.GetManifestResourceStream("lib." + name + ".dll"))
            {
                if (stream == null) return null;
                byte[] bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) return null;
                    offset += read;
                }

                return Assembly.Load(bytes);
            }
        }
    }
}
