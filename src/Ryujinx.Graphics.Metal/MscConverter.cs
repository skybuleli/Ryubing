using System.Diagnostics;

namespace Ryujinx.Graphics.Metal
{
    public sealed class MetalCompiledShader
    {
        public byte[] Metallib { get; }
        public string ReflectionJson { get; }

        public MetalCompiledShader(byte[] metallib, string reflectionJson)
        {
            Metallib = metallib;
            ReflectionJson = reflectionJson;
        }
    }

    static class MscConverter
    {
        public static MetalCompiledShader Convert(byte[] dxilBytes)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "ryubing-msc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string dxilPath = Path.Combine(tmpDir, "shader.dxil");
                string metallibPath = Path.Combine(tmpDir, "shader.metallib");
                string reflectionPath = Path.Combine(tmpDir, "shader.reflection.json");
                File.WriteAllBytes(dxilPath, dxilBytes);

                // MSC uses a top-level argument buffer. The reflection file is required to
                // locate the automatic linear resource descriptors at runtime.
                var psi = new ProcessStartInfo(
                    "metal-shaderconverter",
                    $"\"{dxilPath}\" -o \"{metallibPath}\" --output-reflection-file \"{reflectionPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var proc = Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException($"metal-shaderconverter 转换失败 exit={proc.ExitCode} stderr={stderr} stdout={stdout}");
                }
                if (!File.Exists(metallibPath))
                {
                    throw new FileNotFoundException($"MSC 未生成 metallib: {metallibPath} stderr={stderr}");
                }
                if (!File.Exists(reflectionPath))
                {
                    throw new FileNotFoundException($"MSC 未生成 reflection: {reflectionPath} stderr={stderr}");
                }
                string reflectionJson = File.ReadAllText(reflectionPath);
                string dumpPath = Environment.GetEnvironmentVariable("RYUJINX_METAL_SHADER_DUMP_PATH");
                if (!string.IsNullOrWhiteSpace(dumpPath))
                {
                    Directory.CreateDirectory(dumpPath);
                    File.WriteAllText(Path.Combine(dumpPath, $"msc-{Guid.NewGuid():N}.reflection.json"), reflectionJson);
                }
                return new MetalCompiledShader(File.ReadAllBytes(metallibPath), reflectionJson);
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }
    }
}
