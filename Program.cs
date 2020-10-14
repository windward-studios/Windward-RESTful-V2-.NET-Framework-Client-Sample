/*
 * Copyright (c) 2020 by Windward Studios, Inc. All rights reserved.
 *
 * This program can be copied or used in any manner desired.
 */

using System;
using System.Collections.Generic;
using System.Net;
using log4net.Config;
using System.IO;
using Exception = System.Exception;
using System.Collections;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Console = System.Console;
using Convert = System.Convert;
using File = System.IO.File;
using WindwardRestApi.src.Api;
using WindwardRestApi.src.Model;

namespace RunReport
{
    public class Program
	{
		private static WindwardClient client;

		private static readonly ILog logWriter = LogManager.GetLogger(typeof(Program));

		static async Task Main(string[] args)
		{

			// get connected
			string url = ConfigurationManager.AppSettings["url"];
			if ((!url.EndsWith("/")) && (!url.EndsWith("\\")))
				url += "/";
			Console.Out.WriteLine($"Connecting to URL {url}");
			client = new WindwardClient(new Uri(url));
			VersionInfo version = await client.GetVersion();
			Console.Out.WriteLine($"REST server version = {version}");

			// if no arguments, then we list out the usage.
			if (args.Length < 2)
			{
				DisplayUsage();
				return;
			}

			// the try here is so we can print out an exception if it is thrown. This code does minimal error checking and no other
			// exception handling to keep it simple & clear.
			try
			{
				Console.Out.WriteLine("Running in {0}-bit mode", IntPtr.Size * 8);

				// parse the arguments passed in. This method makes no calls to Windward, it merely organizes the passed in arguments.
				CommandLine cmdLine = CommandLine.Factory(args);

				// This turns on log4net logging. You can also call log4net.Config.XmlConfigurator.Configure(); directly.
				// If you do not make this call, then there will be no logging (which you may want off).
				var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
				XmlConfigurator.Configure(logRepository, new FileInfo("RunReport.config"));
				logWriter.Info($"Starting RunReport ({string.Join(", ", args)})");

				DateTime startTime = DateTime.Now;

				// run one report
				if (!cmdLine.IsPerformance)
				{
					PerfCounters perfCounters = await RunOneReport(cmdLine, args.Length == 2);
					PrintPerformanceCounter(startTime, perfCounters, false);
				}
				else
				{
					string dirReports = Path.GetDirectoryName(Path.GetFullPath(cmdLine.ReportFilename)) ?? "";
					if (!Directory.Exists(dirReports))
					{
						Console.Error.WriteLine($"The directory {dirReports} does not exist");
						return;
					}

					// drop out threads - twice the number of cores.
					int numThreads = cmdLine.NumThreads;
					numReportsRemaining = cmdLine.NumReports;
					ReportWorker[] workers = new ReportWorker[numThreads];
					for (int ind = 0; ind < numThreads; ind++)
						workers[ind] = new ReportWorker(ind, new CommandLine(cmdLine));
					Task[] threads = new Task[numThreads];
					for (int ind = 0; ind < numThreads; ind++)
						threads[ind] = workers[ind].DoWork();
//					for (int ind = 0; ind < numThreads; ind++)
//						threads[ind].Name = "Report Worker " + ind;

					Console.Out.WriteLine($"Start time: {startTime.ToLongTimeString()}, {numThreads} threads, {cmdLine.NumReports} reports");
					Console.Out.WriteLine();
					for (int ind = 0; ind < numThreads; ind++)
						threads[ind].Start();

					// we wait
                    await Task.WhenAll(threads);

					PerfCounters perfCounters = new PerfCounters();
					for (int ind = 0; ind < numThreads; ind++)
						perfCounters.Add(workers[ind].perfCounters);

					Console.Out.WriteLine();
					PrintPerformanceCounter(startTime, perfCounters, true);
				}
			}
			catch (Exception ex)
			{
				logWriter.Error("RunReport", ex);
				string indent = "  ";
				Console.Error.WriteLine();
				Console.Error.WriteLine("Error(s) running the report");
				while (ex != null)
				{
					Console.Error.WriteLine($"{indent}Error: {ex.Message} ({ex.GetType().FullName})\n{indent}     stack: {ex.StackTrace}\n");
					ex = ex.InnerException;
					indent += "  ";
				}
				throw;
			}
		}

		private static void PrintPerformanceCounter(DateTime startTime, PerfCounters perfCounters, bool multiThreaded)
		{

			DateTime endTime = DateTime.Now;
			TimeSpan elapsed = endTime - startTime;

			Console.Out.WriteLine("End time: {0}", endTime.ToLongTimeString());
			Console.Out.WriteLine($"Elapsed time: {elapsed}");
			Console.Out.WriteLine("Time per report: {0}", perfCounters.numReports == 0 ? "n/a" : "" + TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / perfCounters.numReports));
			Console.Out.WriteLine("Pages/report: " + (perfCounters.numReports == 0 ? "n/a" : "" + perfCounters.numPages / perfCounters.numReports));
			Console.Out.WriteLine("Pages/sec: {0:N2}", perfCounters.numPages / elapsed.TotalSeconds);
			if (multiThreaded)
				Console.Out.WriteLine("Below values are totaled across all threads (and so add up to more than the elapsed time)");
			Console.Out.WriteLine($"  Process: {perfCounters.timeProcess}");
		}


		private static int numReportsRemaining;

		private static bool HasNextReport => Interlocked.Decrement(ref numReportsRemaining) >= 0;

		private class ReportWorker
		{
			private readonly int threadNum;
			private readonly CommandLine cmdLine;
			internal PerfCounters perfCounters;

			public ReportWorker(int threadNum, CommandLine cmdLine)
			{
				this.threadNum = threadNum;
				this.cmdLine = cmdLine;
				perfCounters = new PerfCounters();
			}

			public async Task DoWork()
			{
				while (HasNextReport)
				{
					Console.Out.Write($"{threadNum}.");
					PerfCounters pc = await RunOneReport(cmdLine, false);
					perfCounters.Add(pc);
				}
			}
		}

		private static readonly string[] extImages = { "bmp", "eps", "gif", "jpg", "png", "svg" };
		private static readonly string[] extHtmls = { "htm", "html", "xhtml" };

		private static async Task<PerfCounters> RunOneReport(CommandLine cmdLine, bool preservePodFraming)
		{

			DateTime startTime = DateTime.Now;
			PerfCounters perfCounters = new PerfCounters();


			// Create the template object, based on the file extension
			Template.OutputFormatEnum formatOutput = Path.GetExtension(cmdLine.ReportFilename).Substring(1).GetEnumFromValue<Template.OutputFormatEnum>();
			Template.FormatEnum formatTemplate = Path.GetExtension(cmdLine.TemplateFilename).Substring(1).GetEnumFromValue<Template.FormatEnum>();
			Template template = new Template(formatOutput, File.ReadAllBytes(cmdLine.TemplateFilename), formatTemplate);

			template.TrackErrors = cmdLine.VerifyFlag;
			if (!string.IsNullOrEmpty(cmdLine.Locale))
				template.Properties.Add(new Property("report.locale", cmdLine.Locale));

			foreach (KeyValuePair<string, object> pair in cmdLine.Parameters)
				template.Parameters.Add(new Parameter(pair.Key, pair.Value));

			// Now for each datasource, we apply it to the report. This is complex because it handles all datasource types
			foreach (CommandLine.DatasourceInfo dsInfo in cmdLine.Datasources)
			{
				// build the datasource
				switch (dsInfo.Type)
				{
					// An XPATH 2.0 datasource.
					case CommandLine.DatasourceInfo.TYPE.XML:
						if (!cmdLine.IsPerformance)
						{
							if (string.IsNullOrEmpty(dsInfo.SchemaFilename))
								Console.Out.WriteLine(string.Format("XPath 2.0 datasource: {0}", dsInfo.Filename));
							else
								Console.Out.WriteLine(string.Format("XPath 2.0 datasource: {0}, schema {1}", dsInfo.Filename,
									dsInfo.SchemaFilename));
						}

						if (string.IsNullOrEmpty(dsInfo.SchemaFilename))
							template.Datasources.Add(new Xml_20DataSource(dsInfo.Name, File.ReadAllBytes(dsInfo.ExConnectionString), null));
						else
							template.Datasources.Add(new Xml_20DataSource(dsInfo.Name, File.ReadAllBytes(dsInfo.ExConnectionString), File.ReadAllBytes(dsInfo.SchemaFilename)));
						break;

					// An XPATH 1.0 datasource.
					case CommandLine.DatasourceInfo.TYPE.XPATH_1:
						if (!cmdLine.IsPerformance)
						{
							if (string.IsNullOrEmpty(dsInfo.SchemaFilename))
								Console.Out.WriteLine(string.Format("XPath 1.0 datasource: {0}", dsInfo.Filename));
							else
								Console.Out.WriteLine(string.Format("XPath 1.0 datasource: {0}, schema {1}", dsInfo.Filename,
									dsInfo.SchemaFilename));
						}

						if (string.IsNullOrEmpty(dsInfo.SchemaFilename))
							template.Datasources.Add(new Xml_10DataSource(dsInfo.Name, File.ReadAllBytes(dsInfo.ExConnectionString), null));
						else
							template.Datasources.Add(new Xml_10DataSource(dsInfo.Name, File.ReadAllBytes(dsInfo.ExConnectionString), File.ReadAllBytes(dsInfo.SchemaFilename)));
						break;

					case CommandLine.DatasourceInfo.TYPE.JSON:
						if (!cmdLine.IsPerformance)
							Console.Out.WriteLine($"JSON datasource: {dsInfo.Filename}");
						template.Datasources.Add(new JsonDataSource(dsInfo.Name, File.ReadAllBytes(dsInfo.ExConnectionString)));
						break;


					// An OData datasource.
					case CommandLine.DatasourceInfo.TYPE.ODATA:
						if (!cmdLine.IsPerformance)
							Console.Out.WriteLine(string.Format("OData datasource: {0}", dsInfo.Filename));
						template.Datasources.Add(new ODataDataSource(dsInfo.Name, dsInfo.ExConnectionString));
						break;

					// A SalesForce datasource.
					case CommandLine.DatasourceInfo.TYPE.SFORCE:
						if (!cmdLine.IsPerformance)
							Console.Out.WriteLine(string.Format("SalesForce datasource: {0}", dsInfo.Filename));
						template.Datasources.Add(new SalesforceDataSource(dsInfo.Name, dsInfo.ExConnectionString));
						break;

					case CommandLine.DatasourceInfo.TYPE.SQL:
						if (!cmdLine.IsPerformance)
							Console.Out.WriteLine(string.Format("{0} datasource: {1}", dsInfo.SqlDriverInfo.Name,
								dsInfo.ConnectionString));
						template.Datasources.Add(new SqlDataSource(dsInfo.Name, dsInfo.SqlDriverInfo.Classname, dsInfo.ConnectionString));
						break;

					default:
						throw new ArgumentException(string.Format("Unknown datasource type {0}", dsInfo.Type));
				}
			}

			if (!cmdLine.IsPerformance)
				Console.Out.WriteLine("Calling REST engine to start generating report");

			// we have nothing else to do, so we wait till we get the result.
			Document document = await client.PostDocument(template);
			string guid = document.Guid;

			if (!cmdLine.IsPerformance)
				Console.Out.WriteLine($"REST Engine has accepted job {guid}");

			// wait for it to complete
			// instead of this you can use: template.Callback = "http://localhost/alldone/{guid}";
            HttpStatusCode status = await client.GetDocumentStatus(guid);
            while (status == HttpStatusCode.Created || status == HttpStatusCode.Accepted)
            {
                await Task.Delay(100);
                status = await client.GetDocumentStatus(guid);
            }

			// we have nothing else to do, so we wait till we get the result.
			document = await client.GetDocument(guid);

			// delete it off the server
			await client.DeleteDocument(guid);

			if (!cmdLine.IsPerformance)
				Console.Out.WriteLine($"REST Engine has completed job {document.Guid}");

			perfCounters.timeProcess = DateTime.Now - startTime;
			perfCounters.numPages = document.NumberOfPages;
			perfCounters.numReports = 1;

			PrintVerify(document);

			// save
			if (document.Data != null)
				File.WriteAllBytes(cmdLine.ReportFilename, document.Data);
			else
			{
				{
					string prefix = Path.GetFileNameWithoutExtension(cmdLine.ReportFilename);
					string directory = Path.GetDirectoryName(Path.GetFullPath(cmdLine.ReportFilename));
					string extension = Path.GetExtension(cmdLine.ReportFilename);
					for (int fileNumber = 0; fileNumber < document.Pages.Length; fileNumber++)
					{
						string filename = Path.Combine(directory, prefix + "_" + fileNumber + extension);
						filename = Path.GetFullPath(filename);
						File.WriteAllBytes(filename, document.Pages[fileNumber]);
						Console.Out.WriteLine("  document page written to " + filename);
					}
				}
			}

			if (!cmdLine.IsPerformance)
			{
				Console.Out.WriteLine($"{cmdLine.ReportFilename} built, {document.NumberOfPages} pages long.");
				Console.Out.WriteLine($"Elapsed time: {DateTime.Now - startTime}");
			}

			if (cmdLine.Launch)
			{
				Console.Out.WriteLine(string.Format("launching report {0}", cmdLine.ReportFilename));
				System.Diagnostics.Process.Start(cmdLine.ReportFilename);
			}
			return perfCounters;
		}

		private static void PrintVerify(Document document)
		{
			if (document.Errors != null)
				foreach (Issue issue in document.Errors)
					Console.Error.WriteLine(issue.Message);
		}

		private static void DisplayUsage()
		{
			Console.Out.WriteLine("Windward Reports REST Engline C# Client API");
			Console.Out.WriteLine("usage: RunReport template_file output_file [-basedir path] [-xml xml_file | -sql connection_string | -sforce | -oracle connection_string | -ole oledb_connection_string] [key=value | ...]");
			Console.Out.WriteLine("       The template file can be a docx, pptx, or xlsx file.");
			Console.Out.WriteLine("       The output file extension determines the report type created:");
			Console.Out.WriteLine("           output.csv - SpreadSheet CSV file");
			Console.Out.WriteLine("           output.docm - Word DOCM file");
			Console.Out.WriteLine("           output.docx - Word DOCX file");
			Console.Out.WriteLine("           output.htm - HTML file with no CSS");
			Console.Out.WriteLine("           output.html - HTML file with CSS");
			Console.Out.WriteLine("           output.pdf - Acrobat PDF file");
			Console.Out.WriteLine("           output.pptm - PowerPoint PPTM file");
			Console.Out.WriteLine("           output.pptx - PowerPoint PPTX file");
			Console.Out.WriteLine("           output.prn - Printer where \"output\" is the printer name");
			Console.Out.WriteLine("           output.rtf - Rich Text Format file");
			Console.Out.WriteLine("           output.txt - Ascii text file");
			Console.Out.WriteLine("           output.xhtml - XHTML file with CSS");
			Console.Out.WriteLine("           output.xlsm - Excel XLSM file");
			Console.Out.WriteLine("           output.xlsx - Excel XLSX file");
			Console.Out.WriteLine("       -launch - will launch the report when complete.");
			Console.Out.WriteLine("       -performance:123 - will run the report 123 times.");
			Console.Out.WriteLine("            output file is used for directory and extension for reports");
			Console.Out.WriteLine("       -threads:4 - will create 4 threads when running -performance.");
			Console.Out.WriteLine("       -verify:N - turn on the error handling and verify feature where N is a number: 0 (none) , 1 (track errors), 2 (verify), 3 (all).  The list of issues is printed to the standard error.");
			Console.Out.WriteLine("       -version=9 - sets the template to the passed version (9 in this example).");
			Console.Out.WriteLine("       encoding=UTF-8 (or other) - set BEFORE datasource to specify an encoding.");
			Console.Out.WriteLine("       locale=en_US - set the locale passed to the engine.");
			Console.Out.WriteLine("       The datasource is identified with a pair of parameters");
			Console.Out.WriteLine("           -json filename - passes a JSON file as the datasource");
			Console.Out.WriteLine("               filename can be a url/filename or a connection string");
			Console.Out.WriteLine("           -odata url - passes a url as the datasource accessing it using the OData protocol");
			Console.Out.WriteLine("           -sforce - password should be password + security_token (passwordtoken)");
			Console.Out.WriteLine("           -xml filename - XPath 2.0 passes an xml file as the datasource");
			Console.Out.WriteLine("                -xml xmlFilename=schema:schemaFilename - passes an xml file and a schema file as the datasource");
			Console.Out.WriteLine("               filename can be a url/filename or a connection string");
			Console.Out.WriteLine("           -xpath filename - uses the old XPath 1.0 datasource.");
			Console.Out.WriteLine("                -xml xmlFilename=schema:schemaFilename - passes an xml file and a schema file as the datasource");
			Console.Out.WriteLine("               filename can be a url/filename or a connection string");
			foreach (AdoDriverInfo di in AdoDriverInfo.AdoConnectors)
				Console.Out.WriteLine("           -" + di.Name + " connection_string ex: " + di.Example);
			Console.Out.WriteLine("               if a datasource is named you use the syntax -type:name (ex: -xml:name filename.xml)");
			Console.Out.WriteLine("       You can have 0-N key=value pairs that are passed to the datasource Map property");
			Console.Out.WriteLine("            If the value starts with I', F', or D' it parses it as an integer, float, or date(yyyy-MM-ddThh:mm:ss)");
			Console.Out.WriteLine("            If the value is * it will set a filter of all");
			Console.Out.WriteLine("            If the value is \"text,text,...\" it will set a filter of all");
		}
	}

	/// <summary>
	/// This class contains everything passed in the command line. It makes no calls to Windward Reports.
	/// </summary>
	internal class CommandLine
	{

		private byte[] templateFile;

		/// <summary>
		/// Create the object.
		/// </summary>
		/// <param name="templateFilename">The name of the template file.</param>
		/// <param name="reportFilename">The name of the report file. null for printer reports.</param>
		public CommandLine(string templateFilename, string reportFilename)
		{
			TemplateFilename = Path.GetFullPath(templateFilename);
			if (!reportFilename.ToLower().EndsWith(".prn"))
				reportFilename = Path.GetFullPath(reportFilename);
			Launch = false;
			ReportFilename = reportFilename;
			Parameters = new Dictionary<string, object>();
			Datasources = new List<DatasourceInfo>();
			NumThreads = Environment.ProcessorCount * 2;
			VerifyFlag = 0;
		}

		public CommandLine(CommandLine src)
		{
			TemplateFilename = src.TemplateFilename;
			ReportFilename = src.ReportFilename;
			Parameters = src.Parameters == null ? null : new Dictionary<string, object>(src.Parameters);
			Datasources = src.Datasources == null ? null : new List<DatasourceInfo>(src.Datasources);
			DataProviders = src.DataProviders == null ? null : new Dictionary<string, DataSource>(src.DataProviders);
			Locale = src.Locale;
			Launch = src.Launch;
			TemplateVersion = src.TemplateVersion;
			NumReports = src.NumReports;
			NumThreads = src.NumThreads;
			BaseDirectory = src.BaseDirectory;
			VerifyFlag = src.VerifyFlag;
			templateFile = src.templateFile?.ToArray();
		}

		/// <summary>
		/// The name of the template file.
		/// </summary>
		public string TemplateFilename { get; private set; }

		public Stream GetTemplateStream()
		{

			if (TemplateFilename.ToLower().StartsWith("http:") || TemplateFilename.ToLower().StartsWith("https:"))
			{
				WebRequest request = WebRequest.Create(TemplateFilename);
				return request.GetResponse().GetResponseStream();
			}

			if (templateFile == null)
				templateFile = File.ReadAllBytes(TemplateFilename);
			return new MemoryStream(templateFile);
		}

		/// <summary>
		/// The name of the report file. null for printer reports.
		/// </summary>
		public string ReportFilename { get; private set; }

		/// <summary>
		/// The output stream for the report.
		/// </summary>
		public Stream GetOutputStream()
		{
			if (!IsPerformance)
				return new FileStream(ReportFilename, FileMode.Create, FileAccess.Write, FileShare.None);
			string dirReports = Path.GetDirectoryName(ReportFilename) ?? "";
			string extReport = ReportFilename.Substring(ReportFilename.LastIndexOf('.'));
			string filename = Path.GetFullPath(Path.Combine(dirReports, "rpt_" + Guid.NewGuid()) + extReport);
			return new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None);
		}

		/// <summary>
		/// If we are caching the data providers, this is them for passes 1 .. N (set on pass 0)
		/// </summary>
		public IDictionary<string, DataSource> DataProviders { get; set; }

		/// <summary>
		/// true if launch the app at the end.
		/// </summary>
		public bool Launch { get; private set; }

		/// <summary>
		/// The template version number. 0 if not set.
		/// </summary>
		public int TemplateVersion { get; private set; }

		/// <summary>
		/// The name/value pairs for variables passed to the datasources.
		/// </summary>
		public Dictionary<string, object> Parameters { get; private set; }

		/// <summary>
		/// The parameters passed for each datasource to be created.
		/// </summary>
		public List<DatasourceInfo> Datasources { get; private set; }

		/// <summary>
		/// The locale to run under.
		/// </summary>
		public string Locale { get; private set; }

		/// <summary>
		/// For performance modeling, how many reports to run.
		/// </summary>
		public int NumReports { get; private set; }

		/// <summary>
		/// true if requesting a performance run
		/// </summary>
		public bool IsPerformance => NumReports != 0;

		/// <summary>
		/// The number of threads to launch if running a performance test.
		/// </summary>
		public int NumThreads { get; private set; }

		public int VerifyFlag { get; private set; }

		/// <summary>
		/// Base directory.
		/// </summary>
		public string BaseDirectory { get; private set; }

		/// <summary>
		/// The parameters passed for a single datasource. All filenames are expanded to full paths so that if an exception is
		/// thrown you know exactly where the file is.
		/// </summary>
		internal class DatasourceInfo
		{
			/// <summary>
			/// What type of datasource.
			/// </summary>
			internal enum TYPE
			{
				/// <summary>
				/// Use the REST protocol passing the credentials on the first request.
				/// </summary>
				REST,
				/// <summary>
				/// A SQL database.
				/// </summary>
				SQL,
				/// <summary>
				/// An XML file using Saxon.
				/// </summary>
				XML,
				/// <summary>
				/// An XML file using the .NET XPath library.
				/// </summary>
				XPATH_1,
				/// <summary>
				/// An OData url.
				/// </summary>
				ODATA,
				/// <summary>
				/// JSON data source.
				/// </summary>
				JSON,
				/// <summary>
				/// SalesForce data source
				/// </summary>
				SFORCE,
				/// <summary>
				/// A data set
				/// </summary>
				DATA_SET
			}

			private readonly TYPE type;
			private readonly string name;

			private readonly string filename;
			private readonly string schemaFilename;

			private readonly AdoDriverInfo sqlDriverInfo;
			private readonly string connectionString;

			private readonly string username;
			private readonly string password;
			private readonly string securitytoken;//only used for sforce
			private readonly string podFilename;
			private readonly string encoding;
			public bool restful;

			/// <summary>
			/// Create the object for a PLAYBACK datasource.
			/// </summary>
			/// <param name="filename">The playback filename.</param>
			/// <param name="type">What type of datasource.</param>
			public DatasourceInfo(string filename, TYPE type)
			{
				this.filename = Path.GetFullPath(filename);
				this.type = type;
			}

			/// <summary>
			/// Create the object for a XML datasource.
			/// </summary>
			/// <param name="name">The name for this datasource.</param>
			/// <param name="filename">The XML filename.</param>
			/// <param name="schemaFilename">The XML schema filename. null if no schema.</param>
			/// <param name="username">The username if credentials are needed to access the datasource.</param>
			/// <param name="password">The password if credentials are needed to access the datasource.</param>
			/// <param name="podFilename">The POD filename if datasets are being passed.</param>
			/// <param name="type">What type of datasource.</param>
			public DatasourceInfo(string name, string filename, string schemaFilename, string username, string password, string podFilename, TYPE type)
			{
				this.name = name ?? string.Empty;
				this.filename = GetFullPath(filename);
				if (!string.IsNullOrEmpty(schemaFilename))
					this.schemaFilename = GetFullPath(schemaFilename);
				this.username = username;
				this.password = password;
				if (!string.IsNullOrEmpty(podFilename))
					this.podFilename = GetFullPath(podFilename);
				this.type = type;
			}

			/// <summary>
			/// Create the object for a JSON datasource.
			/// </summary>
			/// <param name="name">The name for this datasource.</param>
			/// <param name="filename">The XML filename.</param>
			/// <param name="schemaFilename">The XML schema filename. null if no schema.</param>
			/// <param name="encoding">Theencoding of the JSON file.</param>
			/// <param name="type">What type of datasource.</param>
			public DatasourceInfo(string name, string filename, string schemaFilename, string encoding, TYPE type)
			{
				this.name = name ?? string.Empty;
				this.filename = GetFullPath(filename);
				if (!string.IsNullOrEmpty(schemaFilename))
					this.schemaFilename = GetFullPath(schemaFilename);
				this.encoding = encoding;
				this.type = type;
			}

			/// <summary>
			/// Copy constructor. Does a deep copy.
			/// </summary>
			/// <param name="src">Initialize with the values in this object.</param>
			public DatasourceInfo(DatasourceInfo src)
			{
				type = src.type;
				name = src.name;
				filename = src.filename;
				schemaFilename = src.schemaFilename;
				sqlDriverInfo = src.sqlDriverInfo;
				connectionString = src.connectionString;
				username = src.username;
				password = src.password;
				securitytoken = src.securitytoken;
				podFilename = src.podFilename;
				encoding = src.encoding;
				restful = src.restful;
			}

			private static string GetFullPath(string filename)
			{
				int pos = filename.IndexOf(':');
				if ((pos == -1) || (pos == 1))
					return Path.GetFullPath(filename);
				return filename;
			}

			/// <summary>
			/// Create the object for a SQL datasource.
			/// </summary>
			/// <param name="name">The name for this datasource.</param>
			/// <param name="sqlDriverInfo">The DriverInfo for the selected SQL vendor.</param>
			/// <param name="connectionString">The connection string to connect to the database.</param>
			/// <param name="username">The username if credentials are needed to access the datasource.</param>
			/// <param name="password">The password if credentials are needed to access the datasource.</param>
			/// <param name="podFilename">The POD filename if datasets are being passed.</param>
			/// <param name="type">What type of datasource.</param>
			public DatasourceInfo(string name, AdoDriverInfo sqlDriverInfo, string connectionString, string username, string password, string podFilename, TYPE type)
			{
				this.name = name;
				this.sqlDriverInfo = sqlDriverInfo;
				this.connectionString = connectionString.Trim();
				this.username = username;
				this.password = password;
				if (!string.IsNullOrEmpty(podFilename))
					this.podFilename = Path.GetFullPath(podFilename);
				this.type = type;
			}

			/// <summary>
			/// What type of datasource.
			/// </summary>
			public TYPE Type => type;

			/// <summary>
			/// The name for this datasource.
			/// </summary>
			public string Name => name;

			/// <summary>
			/// The XML or playback filename.
			/// </summary>
			public string Filename => filename;

			/// <summary>
			/// The XML schema filename. null if no schema.
			/// </summary>
			public string SchemaFilename => schemaFilename;

			/// <summary>
			/// The DriverInfo for the selected SQL vendor.
			/// </summary>
			public AdoDriverInfo SqlDriverInfo => sqlDriverInfo;

			/// <summary>
			/// The connection string to connect to the database.
			/// </summary>
			public string ConnectionString => connectionString;

			/// <summary>
			/// The connection string to connect to the database.
			/// </summary>
			public string ExConnectionString => filename;

			/// <summary>
			/// The username if credentials are needed to access the datasource.
			/// </summary>
			public string Username => username;

			/// <summary>
			/// The password if credentials are needed to access the datasource.
			/// </summary>
			public string Password => password;

			/// <summary>
			/// The POD filename if datasets are being passed.
			/// </summary>
			public string PodFilename => podFilename;

			public string Encoding => encoding;
		}

		/// <summary>
		/// Create a CommandLine object from the command line passed to the program.
		/// </summary>
		/// <param name="args">The arguments passed to the program.</param>
		/// <returns>A CommandLine object populated from the args.</returns>
		public static CommandLine Factory(IList<string> args)
		{

			CommandLine rtn = new CommandLine(args[0], args[1]);

			string username = null, password = null, podFilename = null, encoding = null;

			for (int ind = 2; ind < args.Count; ind++)
			{
				string[] sa = args[ind].Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
				string cmd = sa[0].Trim();
				string name = sa.Length < 2 ? "" : sa[1].Trim();

				if (cmd == "-performance")
				{
					rtn.NumReports = int.Parse(name);
					continue;
				}

				if (cmd == "-threads")
				{
					rtn.NumThreads = int.Parse(name);
					continue;
				}

				if (cmd == "-verify")
				{
					rtn.VerifyFlag = int.Parse(name);
					continue;
				}

				if (cmd == "-launch")
				{
					rtn.Launch = true;
					continue;
				}

				if (cmd == "-basedir")
				{
					rtn.BaseDirectory = args[++ind];
					continue;
				}

				if (cmd == "-rest")
				{
					if (rtn.Datasources.Count > 0)
						rtn.Datasources[rtn.Datasources.Count - 1].restful = true;
					continue;
				}

				if ((cmd == "-xml") || (cmd == "-xpath"))
				{
					string filename = args[++ind];
					string schemaFilename = null;
					int split = filename.IndexOf("=schema:");
					if (split == -1)
						schemaFilename = null;
					else
					{
						schemaFilename = filename.Substring(split + 8).Trim();
						filename = filename.Substring(0, split).Trim();
					}
					DatasourceInfo.TYPE type = (cmd == "-rest" ? DatasourceInfo.TYPE.REST
						: (cmd == "-xpath" ? DatasourceInfo.TYPE.XPATH_1 : DatasourceInfo.TYPE.XML));
					DatasourceInfo datasourceOn = new DatasourceInfo(name, filename, schemaFilename, username, password, podFilename, type);
					rtn.Datasources.Add(datasourceOn);
					username = password = podFilename = null;
					continue;
				}

				if (cmd == "-odata")
				{
					string url = args[++ind];
					DatasourceInfo datasourceOn = new DatasourceInfo(name, url, null, username, password, podFilename, DatasourceInfo.TYPE.ODATA);
					rtn.Datasources.Add(datasourceOn);
					username = password = podFilename = null;
					continue;
				}

				if (cmd == "-json")
				{
					string url = args[++ind];
					DatasourceInfo datasourceOn = new DatasourceInfo(name, url, null, encoding, DatasourceInfo.TYPE.JSON);
					rtn.Datasources.Add(datasourceOn);
					username = password = podFilename = null;
					continue;
				}

				if (cmd == "-dataset")
				{
					string dataSetStr = args[++ind];
					DatasourceInfo dsInfo = new DatasourceInfo(name, dataSetStr, null, null, DatasourceInfo.TYPE.DATA_SET);
					rtn.Datasources.Add(dsInfo);
					username = password = podFilename = null;
					continue;
				}


				bool isDb = false;

				if (cmd == "-sforce")
				{
					string url = "https://login.salesforce.com";
					DatasourceInfo datasourceOn = new DatasourceInfo(name, url, null, username, password, podFilename, DatasourceInfo.TYPE.SFORCE);
					rtn.Datasources.Add(datasourceOn);
					isDb = true;
					username = password = podFilename = null;
				}
				foreach (AdoDriverInfo di in AdoDriverInfo.AdoConnectors)
					if (cmd == "-" + di.Name)
					{
						if (((di.Name == "odbc") || (di.Name == "oledb")) && (IntPtr.Size != 4))
							Console.Out.WriteLine("Warning - some ODBC & OleDB connectors only work in 32-bit mode.");

						DatasourceInfo datasourceOn = new DatasourceInfo(name, di, args[++ind], username, password, podFilename, DatasourceInfo.TYPE.SQL);
						rtn.Datasources.Add(datasourceOn);
						isDb = true;
						username = password = podFilename = null;
						break;
					}
				if (isDb)
					continue;

				// assume this is a key=value
				string[] keyValue = args[ind].Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
				if (keyValue.Length != 2)
					if (keyValue.Length == 1 && args[ind].EndsWith("="))
					{
						// We have a variable with the empty value.
						keyValue = new string[2] { keyValue[0], "" };
					}
					else if (keyValue.Length < 2)
						throw new ArgumentException(string.Format("Unknown option {0}", args[ind]));
					else
					{
						// put the rest together.
						string val = "";
						for (int i = 1; i < keyValue.Length; i++)
						{
							val += keyValue[i];
							if (i < keyValue.Length - 1)
								val += '=';
						}
						keyValue = new string[2] { keyValue[0], val };
					}

				switch (keyValue[0])
				{
					case "locale":
						rtn.Locale = keyValue[1];
						break;
					case "version":
						rtn.TemplateVersion = int.Parse(keyValue[1]);
						break;
					case "username":
						username = keyValue[1];
						break;
					case "password":
						password = keyValue[1];
						break;
					case "pod":
						podFilename = keyValue[1];
						break;
					case "encoding":
						encoding = keyValue[1];
						break;
					default:
						object value;
						// may be a list
						if (keyValue[1].IndexOf(',') != -1)
						{
							string[] tok = keyValue[1].Split(new char[] { ',' });
							IList items = new ArrayList();
							foreach (string elem in tok)
								items.Add(ConvertValue(elem, rtn.Locale));
							value = items;
						}
						else
							value = ConvertValue(keyValue[1], rtn.Locale);
						rtn.Parameters.Add(keyValue[0], value);
						break;
				}
			}

			return rtn;
		}

		private static object ConvertValue(string keyValue, string locale)
		{
			if (keyValue.StartsWith("I'"))
				return Convert.ToInt64(keyValue.Substring(2));
			if (keyValue.StartsWith("F'"))
				return Convert.ToDouble(keyValue.Substring(2));
			if (keyValue.StartsWith("D'"))
				return Convert.ToDateTime(keyValue.Substring(2), (locale == null ? CultureInfo.CurrentCulture : new CultureInfo(locale)));
			return keyValue;
		}
	}

	internal class PerfCounters
	{
		internal TimeSpan timeProcess;
		internal int numReports;
		internal int numPages;

		public void Add(PerfCounters pc)
		{
			timeProcess += pc.timeProcess;
			numReports += pc.numReports;
			numPages += pc.numPages;
		}
	}

	/// <summary>
	/// Information on all known ADO.NET connectors.
	/// </summary>
	internal class AdoDriverInfo
	{
		private readonly string name;
		private readonly string classname;
		private readonly string example;

		/// <summary>
		/// Create the object for a given vendor.
		/// </summary>
		/// <param name="name">The -vendor part in the command line (ex: -sql).</param>
		/// <param name="classname">The classname of the connector.</param>
		/// <param name="example">A sample commandline.</param>
		public AdoDriverInfo(string name, string classname, string example)
		{
			this.name = name;
			this.classname = classname;
			this.example = example;
		}

		/// <summary>
		/// The -vendor part in the command line (ex: -sql).
		/// </summary>
		public string Name => name;

		/// <summary>
		/// The classname of the connector.
		/// </summary>
		public string Classname => classname;

		/// <summary>
		/// A sample commandline.
		/// </summary>
		public string Example => example;

		private static List<AdoDriverInfo> listProviders;

		internal static IList<AdoDriverInfo> AdoConnectors
		{
			get
			{
				if (listProviders != null)
					return AdoDriverInfo.listProviders;
				listProviders = new List<AdoDriverInfo>();

				listProviders.Add(new AdoDriverInfo("db2", "IBM.Data.DB2", "server=db2.windwardreports.com;database=SAMPLE;Uid=demo;Pwd=demo;"));
				listProviders.Add(new AdoDriverInfo("mysql", "MySql.Data.MySqlClient", "server=mysql.windwardreports.com;database=sakila;user id=demo;password=demo;"));
				listProviders.Add(new AdoDriverInfo("odbc", "System.Data.Odbc", "Driver={Sql Server};Server=localhost;Database=Northwind;User ID=test;Password=pass;"));
				listProviders.Add(new AdoDriverInfo("oledb", "System.Data.OleDb", "Provider=sqloledb;Data Source=localhost;Initial Catalog=Northwind;User ID=test;Password=pass;"));
				listProviders.Add(new AdoDriverInfo("oracle", "Oracle.ManagedDataAccess.Client", "Data Source=oracle.windwardreports.com:1521/HR;Persist Security Info=True;Password=HR;User ID=HR"));
				listProviders.Add(new AdoDriverInfo("sql", "System.Data.SqlClient", "Data Source=mssql.windwardreports.com;Initial Catalog=Northwind;user=demo;password=demo;"));
				listProviders.Add(new AdoDriverInfo("redshift", "Npgsql", "HOST=localhost;DATABASE=pagila;USER ID=test;PASSWORD=test;"));
				listProviders.Add(new AdoDriverInfo("postgresql", "Npgsql", "HOST=localhost;DATABASE=pagila;USER ID=test;PASSWORD=test;"));

				listProviders.Sort((adi1, adi2) => adi1.Name.CompareTo(adi2.Name));
				return listProviders;
			}
		}
	}
}