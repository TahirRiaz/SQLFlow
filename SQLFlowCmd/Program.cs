using CommandLine;
using CommandLine.Text;
using Konsole;
using SQLFlowCore.Args;
using SQLFlowCore.Pipeline;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading;

namespace SQLFlowCoreCmd
{
    class Options
    {
        [Option("exec", Required = false,
            HelpText = "Execution Types: flow, node, batch, lineage")]
        public string exec
        {
            get;
            set;
        }

        [Option("flowid", Required = false, Default = "0",
         HelpText = "Exec type flow, Ingestion FlowID")]
        public string flowid
        {
            get;
            set;
        }

        [Option("dbg", Required = false, Default = "1",
           HelpText = "Current debug level")]
        public string dbg
        {
            get;
            set;
        }

        [Option("mode", Required = false, Default = "CMD",
                HelpText = "Execution Mode")]
        public string execmode
        {
            get;
            set;
        }

        [Option("node", Required = false, Default = "",
            HelpText = "Exec type node, node (flowid) to start execution from")]
        public string node
        {
            get;
            set;
        }

        [Option("dir", Required = false, Default = "A",
            HelpText = "Exec type node, execution directions Before (B) or After (A)")]
        public string dir
        {
            get;
            set;
        }

        [Option("exitonerror", Required = false, Default = "true",
            HelpText = "Exec type node, exit execution on first error")]
        public string exitonerror
        {
            get;
            set;
        }

        [Option("batch", Required = false, Default = "",
            HelpText = "Exec type batch, execute batch process")]
        public string batch
        {
            get;
            set;
        }

        [Option("flowtype", Required = false, Default = "",
            HelpText = "Exec type batch, limit batch to flowtype (ing, csv, xml, xls)")]
        public string flowtype
        {
            get;
            set;
        }

        [Option("sysalias", Required = false, Default = "",
            HelpText = "Exec type batch, execute all flows for a SysAlias process")]
        public string sysalias
        {
            get;
            set;
        }


        [Option("dbgFileWithTimeStamp", Required = false, Default = "0",
            HelpText = "Exec type batch, execute all flows for a sysalias")]
        public string dbgFileWithTimeStamp
        {
            get;
            set;
        }

        [Option("all", Required = false, Default = "0",
            HelpText = "Exec type Lineage, fetch Lineage information for all objects")]
        public string all
        {
            get;
            set;
        }

        [Option("alias", Required = false, Default = " ",
            HelpText = "Exec type Lineage, fetch Lineage information for flow objects")]
        public string alias
        {
            get;
            set;
        }


        [Option("scalias", Required = false, Default = " ",
        HelpText = "Exec type Source Code commit")]
        public string scalias
        {
            get;
            set;
        }



        [Option("noofthreads", Required = false, Default = "4",
            HelpText = "Exec type Lineage, No of threads for fetching Lineage ")]
        public string noofthreads
        {
            get;
            set;
        }


        [Option("alldep", Required = false, Default = "0",
            HelpText = "Every possible path leading to the current nodes tree")]
        public string alldep
        {
            get;
            set;
        }

        [Option("allbatches", Required = false, Default = "0",
            HelpText = "Every possible path leading to the current nodes tree")]
        public string allbatches
        {
            get;
            set;
        }

        public string GetUsage()
        {
            var result = Parser.Default.ParseArguments<Options>(new string[] { "--help" });
            return HelpText.RenderUsageText(result);

            //return HelpText.AutoBuild(this,
            // (HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
        }
    }

    class Program
    {
        private static int TotalCount = 0;
        private static int RemainCount = 0;

        private static readonly object ObjLock = new();
        private static SortedDictionary<string, ProgressBar> StatusBars = new();
        //private static StatusBar _status = new StatusBar(); 

        static int Main(string[] args)
        {
            string _conStr = Environment.GetEnvironmentVariable("SQLFlowConStr"); //ConfigurationManager.ConnectionStrings["SQLFlowDB"].ToString();
            string _dbgFilePath = ConfigurationManager.AppSettings["dbgFilePath"];
            string _result = "";
            int _dbg = 2;
            int _flowId = 856;
            string _execMode = "cmd";
            string _all = "0";
            string _node = "";
            string _dir = "A";
            bool _alldep = false;
            bool _allbatches = false;
            bool _exitOnError = true;

            string _batch = "";
            string _sysAlias = "";
            string _flowType = "";
            //var test  = System.AppDomain.CurrentDomain.GetAssemblies();
            //foreach (Assembly asb in test)
            //{
            //    Console.WriteLine(asb.FullName);
            //    string filePath = new Uri(asb.CodeBase).LocalPath;
            //    Console.WriteLine(Path.GetDirectoryName(filePath));
            //}
            int rValue = 0;
            if (args.Length > 0)
            {
                var curDir = Directory.GetCurrentDirectory() + @"\Log\";

                if (_dbgFilePath != ".")
                {
                    curDir = _dbgFilePath;
                }

                Parser.Default.ParseArguments<Options>(args)
                    .WithParsed<Options>(o =>
                    {
                        if (o.exec != null)
                        {
                            if (o.execmode != null)
                            {
                                _execMode = o.execmode;
                            }

                            if (o.dbg != null)
                            {
                                _dbg = int.Parse(o.dbg);
                            }

                            switch (o.exec)
                            {
                                case "flow":
                                    {
                                        //Console.WriteLine("flow");
                                        if (o.flowid != null && o.flowid != "0")
                                        {
                                            _flowId = int.Parse(o.flowid);
                                        }
                                        else
                                        {
                                            Console.WriteLine("FlowId is required");
                                            break;
                                        }
                                        ExecFlowProcess.OnRowsCopied += OnRowsCopied;
                                        ExecFlowProcess.OnFileExported += OnFileExport;
                                        ExecFlowProcess.OnPrcExecuted += OnPrcExecuted;

                                        MemoryStream memoryStream = new MemoryStream();
                                        StreamWriter writer = new StreamWriter(memoryStream);
                                        _result = ExecFlowProcess.Exec(writer, _conStr.Trim(), _flowId, _execMode, _dbg);

                                        break;
                                    }
                                case "node":
                                    {
                                        //Console.WriteLine("node");
                                        if (o.node != null && o.node.Length > 0)
                                        {
                                            _node = o.node;
                                        }
                                        else
                                        {
                                            Console.WriteLine("Node is required");
                                            break;
                                        }

                                        if (o.dir != null && o.dir.Length == 1)
                                        {
                                            _dir = o.dir;
                                        }

                                        if (o.exitonerror != null)
                                        {
                                            _exitOnError = bool.Parse(o.exitonerror);
                                        }

                                        if (o.alldep != null && o.alldep == "1")
                                        {
                                            _alldep = true;
                                        }

                                        if (o.allbatches != null && o.allbatches == "1")
                                        {
                                            _allbatches = true;
                                        }


                                        ExecFlowNode.OnFlowProcessed += OnNodeEvent;
                                        ExecFlowNode.OnRowsCopied += OnRowsCopied;
                                        ExecFlowNode.InvokeIsRunning += InvokeIsRunning;
                                        string path = Directory.GetCurrentDirectory() + $"\\Node_{_node}_{_dir}.txt";
                                        StreamWriter writer = new StreamWriter(path);
                                        _result = ExecFlowNode.Exec(writer, _conStr.Trim(), _node, _dir, _execMode, _exitOnError, _alldep, _allbatches, _dbg);

                                        break;
                                    }
                                case "batch":
                                    {
                                        //Console.WriteLine("batch");
                                        if ((o.batch != null && o.batch.Length > 0) || (o.sysalias != null && o.sysalias.Length > 0))
                                        {
                                            _batch = o.batch;
                                            _sysAlias = o.sysalias;
                                        }
                                        else
                                        {
                                            Console.WriteLine("Batch or SysAlias is required");
                                            break;
                                        }

                                        if (o.flowtype != null && o.flowtype.Length > 1)
                                        {
                                            _flowType = o.flowtype;
                                        }

                                        ExecFlowBatch.OnFlowProcessed += OnBatchEvent;
                                        //SQLFlowCore.Engine.ExecFlowBatch.OnRowsCopied += OnRowsCopied;
                                        //SQLFlowCore.Engine.ExecFlowBatch.InvokeIsRunning += InvokeIsRunning;
                                        string path = Directory.GetCurrentDirectory() + $"\\Batch_{_batch}_{_flowType}.txt";
                                        StreamWriter writer = new StreamWriter(path);
                                        _result = ExecFlowBatch.Exec(writer, _conStr.Trim(), _batch, _flowType, _sysAlias, _execMode, _dbg);

                                        break;
                                    }

                                case "lineage":
                                    {
                                        //Console.WriteLine("batch");
                                        string _alias = "";
                                        int _noofthreads = 6;

                                        if (o.alias != null && o.alias.Length >= 1)
                                        {
                                            _alias = o.alias;
                                        }

                                        if (o.all != null && o.all.Length > 0)
                                        {
                                            _all = o.all;
                                        }

                                        if (o.noofthreads != null && o.noofthreads.Length >= 1)
                                        {
                                            _ = int.Parse(o.noofthreads);
                                        }

                                        ExecLineageMap.OnLineageCalculated += OnLineageEvent;

                                        string path = Directory.GetCurrentDirectory() + $"\\Lineage_{_alias}.txt";
                                        StreamWriter writer = new StreamWriter(path);

                                        _result = ExecLineageMap.Exec(writer, _conStr.Trim(), _all, _alias, _execMode, _noofthreads, _dbg).Result;
                                        break;
                                    }

                                case "sc":
                                    {
                                        //Console.WriteLine("batch");
                                        string _scalias = "";
                                        if (o.scalias != null && o.scalias.Length >= 1)
                                        {
                                            _scalias = o.scalias;
                                        }

                                        if ((o.batch != null && o.batch.Length > 0) || (o.sysalias != null && o.sysalias.Length > 0))
                                        {
                                            _batch = o.batch;
                                        }

                                        string path = Directory.GetCurrentDirectory() + $"\\SourceControl_{_scalias}.txt";

                                        MemoryStream memoryStream = new MemoryStream();
                                        StreamWriter writer = new StreamWriter(memoryStream);

                                        ExecSourceControl.OnObjectScripted += CommitToSourcecontrol_OnObjectScripted;
                                        ExecSourceControl.Exec(writer, _conStr.Trim(), _scalias, _batch);

                                        string result = System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
                                        File.WriteAllTextAsync(path, result);

                                        writer.Close();
                                        writer.Dispose();
                                        memoryStream.Close();
                                        memoryStream.Dispose();
                                        _result = result;
                                        break;
                                    }
                            }

                            if (_dbg >= 1)
                            {
                                Console.BackgroundColor = ConsoleColor.Black;
                                Console.Clear();
                                Console.WriteLine("");
                                Console.WriteLine(_result);
                            }
                            else
                            {
                                Console.BackgroundColor = ConsoleColor.Black;
                                Console.Clear();
                                Console.WriteLine("");
                                Console.WriteLine($" Process executed successfully. Please hit any key to exit");
                            }

                            if (_dbg >= 2)
                            {
                                string TimeStamp = "_" + DateTime.Now.ToString(("yyyyMMddHHmmss"));
                                if (o.dbgFileWithTimeStamp == "0")
                                {
                                    TimeStamp = "";
                                }

                                string file = "";

                                if (_flowId > 0)
                                {
                                    file = $"FLW_{_flowId.ToString()}_dbg{TimeStamp}.txt";
                                }

                                if (_node.Length > 0)
                                {
                                    file = $"NODE_{_node}_dbg{TimeStamp}.txt"; ;
                                }

                                if (_batch.Length > 0)
                                {
                                    file = $"BATCH_{_batch}_dbg{TimeStamp}.txt"; ;
                                }

                                string fileName = file;
                                string fullPath = curDir + fileName;

                                if (!Directory.Exists(curDir))
                                {
                                    Directory.CreateDirectory(curDir);
                                }
                                if (!File.Exists(fullPath))
                                {
                                    using (StreamWriter writer = File.CreateText(fullPath))
                                    {
                                        writer.WriteLine(_result);
                                    }
                                }
                            }
                        }
                        else
                        {
                            Console.BackgroundColor = ConsoleColor.Black;
                            Console.WriteLine("");
                            Console.WriteLine("exec parameter is Required");
                        }
                    });
            }
            else
            {
                var help = Parser.Default.ParseArguments<Options>(new string[] { "--help" });
                Console.WriteLine(HelpText.RenderUsageText(help));
            }
            return rValue;
        }

        private static void ExecFlowProcess_OnFileExported(object sender, EventArgsExport e)
        {
            throw new NotImplementedException();
        }

        private static void CommitToSourcecontrol_OnObjectScripted(object sender, EventArgsSourceControl args)
        {
            string IngStatus = $"Scripted {args.ObjectUrn}";
            if (StatusBars.ContainsKey("L1000") == false)
            {
                ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, (int)args.Total);
                StatusBars.Add("L1000", pb);
                pb.Refresh((int)args.Processed, IngStatus);
            }
            else
            {
                ProgressBar pbTmp = StatusBars["L1000"];
                pbTmp.Refresh((int)args.Processed, IngStatus);
            }
        }

        private static void InvokeIsRunning(object sender, EventArgsInvoke e)
        {
            lock (ObjLock)
            {
                string Invoked = $"##### {e.InvokedObjectName} processing time ({e.TimeSpan}) seconds";
                string ThreadID = "I" + Thread.CurrentThread.ManagedThreadId;
                if (StatusBars.ContainsKey(ThreadID) == false)
                {
                    ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, 100);
                    StatusBars.Add(ThreadID, pb);
                    pb.Refresh((int)50, Invoked);
                }
                else
                {
                    ProgressBar pbTmp = StatusBars[ThreadID];
                    pbTmp.Refresh((int)99, Invoked);
                }
            }
        }

        private static void OnRowsCopied(object sender, EventArgsRowsCopied e)
        {
            lock (ObjLock)
            {
                string IngStatus = $"#### Ingesting {e.RowsInTotal} rows from {e.SrcObjectName} to {e.TrgObjectName}";
                //string ThreadID = "F" + Thread.CurrentThread.ManagedThreadId.ToString();

                string ThreadID = $"{e.SrcObjectName} to {e.TrgObjectName}";

                if (StatusBars.ContainsKey(ThreadID) == false)
                {
                    ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, (int)e.RowsInTotal);
                    StatusBars.Add(ThreadID, pb);
                    pb.Refresh((int)e.RowsProcessed, IngStatus);
                }
                else
                {
                    ProgressBar pbTmp = StatusBars[ThreadID];
                    pbTmp.Refresh((int)e.RowsProcessed, IngStatus);
                }


                //if (StatusBars.ContainsKey(ThreadID) == false)
                //{
                //    ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, (int)e.RowsInTotal);
                //    StatusBars.Add(ThreadID, pb);
                //    pb.Refresh((int)e.RowsProcessed, IngStatus);
                //}
                //else
                //{
                //    ProgressBar pbTmp = StatusBars[ThreadID];
                //    pbTmp.Refresh((int)e.RowsProcessed, IngStatus);
                //}
            }
        }

        public static void OnLineageEvent(object obj, EventArgsLineage args)
        {
            lock (ObjLock)
            {
                string IngStatus = $"{args.ObjectUrn}";
                if (StatusBars.ContainsKey("L1000") == false)
                {
                    ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, (int)args.InTotal);
                    StatusBars.Add("L1000", pb);
                    pb.Refresh((int)args.Processed, IngStatus);
                }
                else
                {
                    ProgressBar pbTmp = StatusBars["L1000"];
                    pbTmp.Refresh((int)args.Processed, IngStatus);
                }
            }
        }

        public static void OnFileExport(object obj, EventArgsExport args)
        {
            lock (ObjLock)
            {
                string IngStatus = $"{args.FullFileName}";
                if (StatusBars.ContainsKey("L1000") == false)
                {
                    ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, (int)args.InTotal);
                    StatusBars.Add("L1000", pb);
                    pb.Refresh((int)args.Processed, IngStatus);
                }
                else
                {
                    ProgressBar pbTmp = StatusBars["L1000"];
                    pbTmp.Refresh((int)args.Processed, IngStatus);
                }
            }
        }

        public static void OnPrcExecuted(object obj, EventArgsPrc args)
        {
            lock (ObjLock)
            {
                if (StatusBars.ContainsKey("L1000") == false)
                {
                    ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, (int)args.InTotal);
                    StatusBars.Add("L1000", pb);
                    pb.Refresh((int)args.Processed, args.Description);
                }
                else
                {
                    ProgressBar pbTmp = StatusBars["L1000"];
                    pbTmp.Refresh((int)args.Processed, args.Description);
                }
            }
        }

        public static void OnBatchEvent(object obj, EventArgsBatch args)
        {
            lock (ObjLock)
            {
                string BatchName =
                    $"## Step: {args.Step}, FlowId: {args.FlowID.ToString()} || SysAlias: {args.SysAlias} || Batch: {args.Batch} || FlowType: {args.FlowType}";
                if (StatusBars.ContainsKey("B2000") == false)
                {
                    ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, (int)args.InTotal);
                    StatusBars.Add("B2000", pb);
                    pb.Refresh((int)args.Processed, BatchName);
                }
                else
                {
                    ProgressBar pbTmp = StatusBars["B2000"];
                    pbTmp.Refresh((int)args.Processed, BatchName);
                }
            }
            /*
            lock (ObjLock)
            {
               
            }*/
            //_status.Report(args.Status, BatchName);
        }

        public static void OnNodeEvent(object obj, EventArgsNode args)
        {
            string NodeName = $"## Processing Node ({args.Node}) || Current FlowId: {args.FlowID.ToString()} || FlowType: {args.FlowType} || Direction: {args.Direction}";
            string ThreadID = "N" + Thread.CurrentThread.ManagedThreadId;
            if (StatusBars.ContainsKey(ThreadID) == false)
            {
                ProgressBar pb = new ProgressBar(PbStyle.DoubleLine, args.InTotal);
                StatusBars.Add(ThreadID, pb);
                pb.Refresh((int)args.Processed, NodeName);
            }
            else
            {
                ProgressBar pbTmp = StatusBars[ThreadID];
                pbTmp.Refresh((int)args.Processed, NodeName);
            }
        }

    }
}
