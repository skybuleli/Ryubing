using System.Diagnostics;

namespace Ryujinx.Graphics.Metal
{
    static class MscConverter
    {
        public static byte[] Convert(byte[] dxilBytes)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "ryubing-msc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string dxilPath = Path.Combine(tmpDir, "shader.dxil");
                string metallibPath = Path.Combine(tmpDir, "shader.metallib");
                File.WriteAllBytes(dxilPath, dxilBytes);

                // 优先 CLI，P/Invoke 直调 libmetalirconverter 在后续迭代中替换
                var psi = new ProcessStartInfo("metal-shaderconverter", $"\"{dxilPath}\" -o \"{metallibPath}\"")
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
                return File.ReadAllBytes(metallibPath);
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }
    }
}
