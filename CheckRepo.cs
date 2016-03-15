using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using Tsul.Network;

namespace CheckRepo
{
	public class RepoFileInfo
	{
		public string Type;
		public string CsumType;
		public string Csum;
		public string Name;
		public long? Size;
	}

	public enum CheckResult
	{
		NotExist = 0,
		BadSize,
		BadHash,
		OK
	}

	class Program
	{
		static Dictionary<string, HashAlgorithm> algs = new Dictionary<string, HashAlgorithm>();
		const int BufSize = 1 << 20;
		const string MainRepoFile = "repodata/repomd.xml";

		static void DownloadProgress(long written, long total)
		{
			Console.Write("\r{0} / {1} ({2:P})  ", written, total, (double)written / total);
		}

		static bool UpdateFile(string baseUrl, string dir, string fname)
		{
			string fullDir = Path.GetDirectoryName( Path.Combine(dir,fname));
			if (!Directory.Exists(fullDir))
				Directory.CreateDirectory(fullDir);
			bool? r = HttpUtils.DownloadFile(baseUrl + fname, dir + fname, DownloadProgress);
			Console.WriteLine(r == true ? "OK" : r == null ? "Error" : "Fatal");
			return true == r;
		}

		static CheckResult CheckFile(string dir, RepoFileInfo f, ref string msg, bool checkHash = true)
		{
			string fname = Path.Combine(dir, f.Name);
			var fi = new FileInfo(fname);
			if (!fi.Exists)
			{
				msg = String.Format("File '{0}' does not exist.", f.Name);
				return CheckResult.NotExist;
			}
			if (f.Size.HasValue && fi.Length != f.Size)
			{
				msg = String.Format("File '{0}' size {1} mismatches, needs {2}.", f.Name, fi.Length, f.Size);
				return CheckResult.BadSize;
			}
			if (checkHash)
			{
				string hType = f.CsumType.ToUpper();
				HashAlgorithm hAlg;
				if (!algs.TryGetValue(hType, out hAlg))
				{
					if (hType == "SHA256")
						hAlg = new SHA256Cng();
					else
						hAlg = HashAlgorithm.Create(hType);
					algs[hType] = hAlg;
				}
				string hash;
				using (var fs = File.OpenRead(fname))
					hash = String.Join("", hAlg.ComputeHash(fs).Select(b => b.ToString("x2")));
				if (f.Csum.ToLower() != hash)
				{
					msg = String.Format("File '{0}' {1}-hash {2} mismatches needed {3}.", f.Name, f.CsumType, hash, f.Csum);
					return CheckResult.BadHash;
				}
			}
			return CheckResult.OK;
		}

		static bool CheckAndUpdate(string url, string dir, RepoFileInfo f, bool checkHash = true)
		{
			string msg = null;
			var r = CheckFile(dir, f, ref msg, checkHash);
			if (r == CheckResult.OK)
				return true;
			if (r != CheckResult.NotExist || url == null)
				Console.WriteLine(msg);
			if (url == null)
				return false;
			Console.WriteLine("Downloading file '{0}' ...", f.Name);
			if (r != CheckResult.NotExist)
				File.Delete(Path.Combine(dir, f.Name));
			if (!UpdateFile(url, dir, f.Name))
			{
				Console.WriteLine("Error");
				return false;
			}
			r = CheckFile(dir, f, ref msg);
			if (r != CheckResult.OK)
			{
				Console.WriteLine(msg);
				return false;
			}
			return true;
		}

		static bool CheckRepo(string dir, string url)
		{
			int badFiles = 0;
			Console.WriteLine("Checking repository at " + dir + (Path.IsPathRooted(dir) ? "" : " (" + Path.GetFullPath(dir) + ")"));
			if (!Directory.Exists(dir))
			{
				Console.WriteLine("Fatal: dir '{0}' does not exist.", dir);
				return false;
			}
			dir = dir.TrimEnd('\\') + '\\';
			if (url == "")
				url = File.ReadLines(dir + ".url").First();
			else if (url != null)
			{
				url = url.TrimEnd('/') + '/';
				File.WriteAllText(dir + ".url", url);
			}
			if (url != null)
				Console.WriteLine("Update from " + url);

			string repomd = Path.Combine(dir, MainRepoFile);
			if (!File.Exists(repomd) && url == null)
			{
				Console.WriteLine("Fatal: Main file '{0}' does not exist.", MainRepoFile);
				return false;
			}
			if (url != null)
			{
				if (File.Exists(repomd))
				{
					var bak = repomd + ".bak";
					File.Delete(bak);
					File.Move(repomd, bak);
				}
				UpdateFile(url, dir, MainRepoFile);
			}
			XNamespace ns = "http://linux.duke.edu/metadata/repo";
			var ff = from data in XDocument.Load(repomd).Element(ns + "repomd").Elements(ns + "data")
					 let csum = data.Element(ns + "checksum")
					 select new RepoFileInfo()
					 {
						 Type = (string)data.Attribute("type"),
						 CsumType = ((string)csum.Attribute("type")),
						 Csum = (string)csum,
						 Name = (string)data.Element(ns + "location").Attribute("href"),
						 Size = (long?)data.Element(ns + "size")
					 };
			string prname = null;
			foreach (var f in ff)
			{
				if (!CheckAndUpdate(url, dir, f))
				{
					badFiles++;
					continue;
				}
				if (f.Type == "primary")
				{
					if (prname == null)
						prname = f.Name;
					else
					{
						Console.WriteLine("Error: Duplicated primary file: {0}, was {1}.", f.Name, prname);
					}
				}
			}
			if (prname == null)
			{
				Console.WriteLine("Fatal: Bad or absent primary filelist.");
				return false;
			}
			else
				badFiles += CheckList(url, dir, prname);
			return badFiles == 0;
		}

		static int CheckList(string url, string dir, string prname)
		{
			int badFiles = 0;
			Console.WriteLine("Checking rpm files...");
			prname = Path.Combine(dir, prname);
			Stream prfs = File.OpenRead(prname);
			if (prname.EndsWith(".gz"))
				prfs = new GZipStream(prfs, CompressionMode.Decompress);
			XNamespace ns = "http://linux.duke.edu/metadata/common";
			var ff = from data in XDocument.Load(prfs).Element(ns + "metadata").Elements(ns + "package")
					 let csum = data.Element(ns + "checksum")
					 select new RepoFileInfo()
					 {
						 Type = (string)data.Attribute("type"),
						 CsumType = ((string)csum.Attribute("type")),
						 Csum = (string)csum,
						 Name = (string)data.Element(ns + "location").Attribute("href"),
						 Size = (long?)data.Element(ns + "size").Attribute("package")
					 };
			foreach (var f in ff)
			{
				if (!CheckAndUpdate(url, dir, f))
					badFiles++;
				else if (f.Type != "rpm")
				{
					Console.WriteLine("Error: File '{0}' is not an RPM.", f.Name);
					badFiles++;
				}
			}
			prfs.Close();
			return badFiles;
		}

		static int Main(string[] args)
		{
			string repoDir = null;
			string updateUrl = null;

			if (args.Length == 0)
				repoDir = ".";
			else
				foreach (var s in args)
				{
					if (s == "-u")
						updateUrl = "";
					else if (updateUrl == "")
						updateUrl = s;
					else
						repoDir = s;
				}

			bool r = CheckRepo(repoDir, updateUrl);
			if (r)
				Console.WriteLine("The repo is OK.");
			else
				Console.WriteLine("Bad repo.");
			return r ? 0 : 1;
		}
	}
}
