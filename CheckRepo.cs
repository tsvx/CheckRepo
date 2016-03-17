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
			string dirName = Path.GetDirectoryName(dir + fname);
			if (!Directory.Exists(dirName))
				Directory.CreateDirectory(dirName);
			bool? r = HttpUtils.DownloadFile(baseUrl + fname, dir + fname, DownloadProgress);
			Console.WriteLine(r == true ? "OK" : r == null ? "Error" : "Fatal");
			return true == r;
		}

		static CheckResult CheckFile(string dir, RepoFileInfo f, ref string msg, bool checkHash)
		{
			string fname = dir + f.Name;
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

		static bool CheckAndUpdate(string url, string dir, RepoFileInfo f, bool checkHash)
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
				File.Delete(dir + f.Name);
			if (!UpdateFile(url, dir, f.Name))
			{
				Console.WriteLine("Error");
				return false;
			}
			r = CheckFile(dir, f, ref msg, checkHash);
			if (r != CheckResult.OK)
			{
				Console.WriteLine(msg);
				return false;
			}
			return true;
		}

		static bool CheckRepo(string dir, string url, SortedSet<string> list, bool checkHash)
		{
			int badFiles = 0;
			Console.WriteLine("Checking repository at " + dir + (Path.IsPathRooted(dir) ? "" : " (" + Path.GetFullPath(dir) + ")"));
			if (!Directory.Exists(dir))
			{
				Console.WriteLine("Fatal: dir '{0}' does not exist.", dir);
				return false;
			}
			string urlFile = dir + ".url";
			if (url == "")
				url = File.ReadLines(urlFile).First();
			else if (url != null)
			{
				url = url.TrimEnd('/') + '/';
				File.WriteAllText(urlFile, url);
			}
			if (url != null)
				Console.WriteLine("Update from " + url);
			string repomd = dir + MainRepoFile;
			if (list != null)
			{
				list.Add(urlFile);
				list.Add(repomd);
			}
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
					if (list != null)
						list.Add(bak);
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
				if (list != null)
					list.Add(dir + f.Name);
				if (!CheckAndUpdate(url, dir, f, checkHash))
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
				badFiles += CheckList(url, dir, prname, list, checkHash);
			return badFiles == 0;
		}

		static int CheckList(string url, string dir, string prname, SortedSet<string> list, bool checkHash)
		{
			int badFiles = 0;
			Console.WriteLine("Checking rpm files...");
			prname = dir + prname;
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
				if (list != null)
					list.Add(dir + f.Name);
				if (!CheckAndUpdate(url, dir, f, checkHash))
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

		static void ShowExcess(string dir, SortedSet<string> keepFiles, bool deleteExcess)
		{
			var allFiles = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
			if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
				allFiles = allFiles.Select(s => s.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			var toDel = new SortedSet<string>(allFiles);
			toDel.ExceptWith(keepFiles);
			if (toDel.Count > 0)
				Console.WriteLine("Excess files:");
			foreach (var f in toDel)
				if (deleteExcess)
				{
					Console.Write(f);
					File.Delete(f);
					Console.WriteLine(" removed.");
				}
				else
					Console.WriteLine(f);
			// TODO: Remove empty dirs.
			if (toDel.Count > 0 && deleteExcess)
				Console.WriteLine("Done.");
		}
		
		static int Main(string[] args)
		{
			string repoDir = null;
			string updateUrl = null;
			SortedSet<string> filesList = null;
			bool deleteExcess = false;
			bool checkHash = false;

			foreach (var s in args)
			{
				if (s == "-r")
					filesList = new SortedSet<string>();
				else if (s == "-rr")
				{
					filesList = new SortedSet<string>();
					deleteExcess = true;
				}
				else if (s == "-c")
					checkHash = true;
				else if (s == "-u")
					updateUrl = "";
				else if (updateUrl == "")
					updateUrl = s;
				else if (Directory.Exists(s))
					repoDir = s;
				else
				{
					Console.WriteLine("Fatal: unknown argument: " + s);
					return 1;
				}
			}
			if (repoDir == null)
				repoDir = ".";

			if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar)
				repoDir = repoDir.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			repoDir = repoDir.TrimEnd('/') + '/';

			bool r = CheckRepo(repoDir, updateUrl, filesList, checkHash);
			if (r)
			{
				Console.WriteLine("The repo is OK.");
				if (filesList != null)
					ShowExcess(repoDir, filesList, deleteExcess);
			}
			else
				Console.WriteLine("Bad repo.");
			return r ? 0 : 1;
		}
	}
}
