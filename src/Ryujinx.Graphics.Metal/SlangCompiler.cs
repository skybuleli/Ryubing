using Ryujinx.Graphics.Shader;
using System.Diagnostics;

namespace Ryujinx.Graphics.Metal
{
    static class SlangCompiler
    {
        public static byte[] Compile(string slangSource, ShaderStage stage)
        {
            string stageArg = StageToSlangArg(stage);
            string tmpDir = Path.Combine(Path.GetTempPath(), "ryubing-slang-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string slangPath = Path.Combine(tmpDir, $"shader.slang");
                string dxilPath = Path.Combine(tmpDir, $"shader.dxil");
                File.WriteAllText(slangPath, slangSource);

                var psi = new ProcessStartInfo("slangc", $"\"{slangPath}\" -target dxil -entry main -stage {stageArg} -profile sm_6_0 -o \"{dxilPath}\"")
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
                    throw new InvalidOperationException($"slangc 编译失败 stage={stage} exit={proc.ExitCode} stderr={stderr} stdout={stdout}");
                }
                if (!File.Exists(dxilPath))
                {
                    throw new FileNotFoundException($"slangc 未生成 dxil: {dxilPath} stderr={stderr}");
                }
                return File.ReadAllBytes(dxilPath);
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        private static string StageToSlangArg(ShaderStage stage) => stage switch
        {
            ShaderStage.Vertex => "vertex",
            ShaderStage.Fragment => "fragment",
            ShaderStage.Compute => "compute",
            ShaderStage.Geometry => "geometry",
            ShaderStage.TessellationControl => "hull",
            ShaderStage.TessellationEvaluation => "domain",
            _ => "vertex",
        };
    }
}
