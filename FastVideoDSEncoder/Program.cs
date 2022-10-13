using System;
using System.Runtime.Intrinsics.X86;
using CommandLine;
using CommandLine.Text;

namespace Gericom.FastVideoDSEncoder
{
    internal class Program
    {
        private class Options
        {
            [Option('j', HelpText = "Number of concurrent jobs (default: cpu threads / 1.5)", MetaValue = "jobs")]
            public int? Jobs { get; set; }

            [Value(0, Required = true, MetaName = "input", HelpText = "Input video file")]
            public string Input { get; set; }

            [Value(1, Required = true, MetaName = "output", HelpText = "Output video file")]
            public string Output { get; set; }
        }

        static void Main(string[] args)
        {
            Console.WriteLine("FastVideoDS Encoder by Gericom");

            if (!Avx2.IsSupported)
            {
                Console.WriteLine();
                Console.WriteLine("This encoder requires a cpu with support for AVX2 instructions");
                return;
            }

            var parser = new Parser(with =>
            {
                with.HelpWriter  = null;
                with.AutoVersion = false;
                with.AutoHelp    = true;
            });

            var parserResult = parser.ParseArguments<Options>(args);

            parserResult.WithParsed(opt =>
            {
                Console.WriteLine();
                FvEncoder.Encode(opt.Input, opt.Output, opt.Jobs ?? (int)Math.Round(Environment.ProcessorCount / 1.5));
            });

            parserResult.WithNotParsed(errs =>
            {
                var helpText = HelpText.AutoBuild(parserResult, h =>
                {
                    h.AdditionalNewLineAfterOption = false;
                    h.Heading                      = "";
                    h.Copyright                    = "";
                    h.AutoVersion                  = false;
                    h.AutoHelp                     = false;
                    h.AddPreOptionsLine("Usage: FastVideoDSEncoder [-j jobs] input output.fv");

                    return h;
                });

                Console.WriteLine(helpText);
            });
        }
    }
}