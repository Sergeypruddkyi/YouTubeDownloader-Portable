using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace YouTubeDownloader
{
    public static class HybridCutRunner
    {
        public sealed class StepResult
        {
            public bool Ok;
            public bool Canceled;
            public string Error = "";
        }

        public sealed class Ctx
        {
            public string Input;
            public string WorkDir;
            public HybridCut.Plan Plan;
            public bool HasAudio;
            public List<string> Parts = new List<string>();
            public Func<bool> IsCancel;
            public Action<double> Progress;
            public StringBuilder ErrTail;
            public double DoneSec;
            public double TotalSec;
        }

        public static HybridCut.Plan LastPlan;

        public static bool IsCanceled(Ctx ctx)
        {
            try { return ctx.IsCancel != null && ctx.IsCancel(); }
            catch { return false; }
        }

        public static void Report(Ctx ctx)
        {
            try
            {
                if (ctx.Progress == null) return;
                double p = ctx.DoneSec / ctx.TotalSec;
                if (p < 0) p = 0;
                if (p > 0.99) p = 0.99;
                ctx.Progress(p);
            }
            catch { }
        }

        public static void NoteErr(Ctx ctx, string line)
        {
            if (line == null) return;
            try { lock (ctx.ErrTail) { if (ctx.ErrTail.Length < 4000) ctx.ErrTail.AppendLine(line); } }
            catch { }
        }

        public static StepResult TryHybrid(string input, string output, HybridCut.Plan plan,
            bool hasAudio, Func<bool> isCancel, Action<double> progress, StringBuilder errTail)
        {
            StepResult fail = new StepResult();
            Ctx ctx = new Ctx();
            ctx.Input = input;
            ctx.Plan = plan;
            ctx.HasAudio = hasAudio;
            ctx.IsCancel = isCancel;
            ctx.Progress = progress;
            ctx.ErrTail = errTail;
            try
            {
                ctx.WorkDir = Path.Combine(Path.GetTempPath(), "YouTubeDownloader",
                    "hybrid_" + Process.GetCurrentProcess().Id + "_" + DateTime.Now.Ticks);
                Directory.CreateDirectory(ctx.WorkDir);
            }
            catch (Exception ex) { fail.Error = "hybrid workdir: " + ex.Message; return fail; }
            try
            {
                string[] zoneArgs = HybridCut.ZoneVideoArgs(plan.VideoCodec);
                if (zoneArgs == null) { fail.Error = "no zone encoder for " + plan.VideoCodec; return fail; }
                double zoneSum = 0;
                double copySum = 0;
                for (int i = 0; i < plan.Regions.Count; i++)
                {
                    zoneSum += plan.HeadEnds[i] - plan.Regions[i].Start;
                    copySum += plan.Regions[i].End - plan.HeadEnds[i];
                }
                ctx.TotalSec = Math.Max(0.001, zoneSum * 20 + copySum * 0.2 + plan.Regions.Count * 1.0);
                ctx.DoneSec = 0;
                for (int i = 0; i < plan.Regions.Count; i++)
                {
                    if (IsCanceled(ctx)) { fail.Canceled = true; return fail; }
                    double rs = plan.Regions[i].Start;
                    double he = plan.HeadEnds[i];
                    double re = plan.Regions[i].End;
                    if (he > rs + 0.0005)
                    {
                        string zp = Path.Combine(ctx.WorkDir, "z" + i + ".mp4");
                        StepResult zr = HybridCutSteps.RunZone(ctx, i, rs, he, zoneArgs);
                        if (!zr.Ok) { fail.Canceled = zr.Canceled; fail.Error = zr.Error; return fail; }
                        ctx.Parts.Add(zp);
                        ctx.DoneSec += (he - rs) * 20;
                        Report(ctx);
                    }
                    if (re > he + 0.0005)
                    {
                        string cp = Path.Combine(ctx.WorkDir, "c" + i + ".mp4");
                        StepResult cr = HybridCutSteps.RunCopy(ctx, he, re, cp);
                        if (!cr.Ok) { fail.Canceled = cr.Canceled; fail.Error = cr.Error; return fail; }
                        ctx.Parts.Add(cp);
                        ctx.DoneSec += (re - he) * 0.2;
                        Report(ctx);
                    }
                    ctx.DoneSec += 0.5;
                    Report(ctx);
                }
                string vcat = Path.Combine(ctx.WorkDir, "vcat.mp4");
                StepResult vr = HybridCutSteps.RunConcatCopy(ctx, vcat);
                if (!vr.Ok) { fail.Canceled = vr.Canceled; fail.Error = vr.Error; return fail; }
                ctx.DoneSec += 0.5 * plan.Regions.Count;
                Report(ctx);
                string afile = null;
                if (hasAudio)
                {
                    afile = Path.Combine(ctx.WorkDir, "a.m4a");
                    // Быстрый ADTS Hybrid Audio; при любой проблеме — graceful
                    // откат на существующий полный RunAudio (видео-гибрид и
                    // глобальный fallback при этом сохраняются).
                    StepResult ar = HybridCutSteps.RunAudioHybridFast(ctx, afile);
                    if (!ar.Ok && !ar.Canceled)
                    {
                        lock (errTail) { errTail.AppendLine("fast audio skipped: " + ar.Error); }
                        try { if (File.Exists(afile)) File.Delete(afile); } catch { }
                        ar = HybridCutSteps.RunAudio(ctx, afile);
                    }
                    if (!ar.Ok) { fail.Canceled = ar.Canceled; fail.Error = ar.Error; return fail; }
                }
                StepResult mr = HybridCutSteps.RunMux(ctx, vcat, afile, output);
                if (!mr.Ok) { fail.Canceled = mr.Canceled; fail.Error = mr.Error; return fail; }
                // Acceptance gate: frame count + decode scan.
                // Любое расхождение → fallback, а не битый файл пользователю.
                string verr = HybridCutVerify.CheckOutput(ctx, output);
                if (verr != null) { fail.Error = verr; return fail; }
                try { if (ctx.Progress != null) ctx.Progress(1.0); }
                catch { }
                StepResult ok = new StepResult();
                ok.Ok = true;
                return ok;
            }
            finally
            {
                try { if (Directory.Exists(ctx.WorkDir)) Directory.Delete(ctx.WorkDir, true); }
                catch { }
            }
        }
    }
}
