﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;

namespace CheckRepo
{
	class Program
	{
		static Dictionary<string, HashAlgorithm> algs = new Dictionary<string, HashAlgorithm>();
		const int BufSize = 1 << 20;

		static void DownloadProgress(long written, long total)
		{
			Console.WriteLine("\r{0} / {1} ({2:P})  ", written, total, (double)written/total);
		}

		static void UpdateFile(string url, string path)
		{
		}

		static bool CheckRepo(string dir, string url)
		{
			Console.WriteLine("Checking repository at " + dir + (Path.IsPathRooted(dir) ? "" : " (" + Path.GetFullPath(dir) + ")"));

			if (url == "")
				url = File.ReadLines(".url").First();
			else if (url != null)
			{
				url = url.TrimEnd('/') + '/';
				File.WriteAllText(".url", url);
			}
			Console.WriteLine("Update from " + url);

			string repomd = Path.Combine(dir, "repodata", "repomd.xml");
			if (url != null)
				UpdateFile(url + "repodata/repomd.xml", repomd);
			XNamespace ns = "http://linux.duke.edu/metadata/repo";
			var ff = from data in XDocument.Load(repomd).Element(ns + "repomd").Elements(ns + "data")
					 let csum = data.Element(ns + "checksum")
					 select new
					 {
						 Type = (string)data.Attribute("type"),
						 CsumType = ((string)csum.Attribute("type")),
						 Csum = (string)csum,
						 Name = (string)data.Element(ns + "location").Attribute("href"),
						 Size = (long?)data.Element(ns + "size")
					 };
			var files = ff.ToArray();
			foreach (var f in files)
			{
				
				string fname = Path.Combine(dir, f.Name);
				if (f.Size.HasValue)
					Trace.Assert((new FileInfo(fname)).Length == f.Size);
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
				Trace.Assert(f.Csum.ToLower() == hash);
			}

			var prname = files.Where(f => f.Type == "primary").Single().Name;
			CheckList(dir, prname);

			Console.WriteLine("All is OK!");
			return true;
		}

		static int CheckList(string path, string prname)
		{
			Console.WriteLine("Checking files...");
			prname = Path.Combine(path, prname);
			Stream prfs = File.OpenRead(prname);
			if (prname.EndsWith(".gz"))
				prfs = new GZipStream(prfs, CompressionMode.Decompress);
			XNamespace ns = "http://linux.duke.edu/metadata/common";
			var ff = from data in XDocument.Load(prfs).Element(ns + "metadata").Elements(ns + "package")
					 let csum = data.Element(ns + "checksum")
					 select new
					 {
						 Type = (string)data.Attribute("type"),
						 CsumType = ((string)csum.Attribute("type")),
						 Csum = (string)csum,
						 Name = (string)data.Element(ns + "location").Attribute("href"),
						 Size = (long?)data.Element(ns + "size").Attribute("package")
					 };
			var files = ff.ToArray();
			prfs.Close();
			//byte[] buf = new byte[BufSize];
			//Stopwatch swf = new Stopwatch(), swh = new Stopwatch();
			long nread = 0;
			foreach (var f in files)
			//Parallel.ForEach(files, f =>
			{
				byte[] buf = new byte[BufSize];
				string fname = Path.Combine(path, f.Name);
				long fsize = (new FileInfo(fname)).Length;
				if (f.Size.HasValue && fsize != f.Size)
					throw new ApplicationException("File " + fname + ": size mismatch");
				string hType = f.CsumType.ToUpper();
				//HashAlgorithm hAlg;
				//if (!algs.TryGetValue(hType, out hAlg))
				//	algs[hType] = hAlg = HashAlgorithm.Create(hType);
				//hAlg.Initialize();
				HashAlgorithm hAlg = HashAlgorithm.Create(hType);
				string hash;
				using (var fs = new FileStream(fname, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
				{
					int nr;
					//swf.Start();
					
					while ((nr = fs.Read(buf, 0, buf.Length)) > 0)
					{
						//swf.Stop();
						Interlocked.Add(ref nread, nr);
						//nread += nr;
						//swh.Start();
						hAlg.TransformBlock(buf, 0, nr, null, 0);
						//swh.Stop();
						//swf.Start();
					}
					//swf.Stop();
					hAlg.TransformFinalBlock(buf, 0, 0);
					hash = String.Join("", hAlg.Hash.Select(b => b.ToString("x2")));
					
					//hash = String.Join("", hAlg.ComputeHash(fs).Select(b => b.ToString("x2")));
				}
				hAlg.Dispose();
				if (f.Csum.ToLower() != hash)
					throw new ApplicationException("File " + fname + ": hash mismatch");
			}
			Trace.Assert(files.All(f => f.Type == "rpm"));
			double dnr = nread / (double)(1 << 20);
			Console.WriteLine("Processed {0} files, {1:F3} MB", files.Length, dnr);
			//Console.WriteLine("Reading: {0:F3} s, {1:F2} MB/s", swf.Elapsed.TotalSeconds, dnr / swf.Elapsed.TotalSeconds);
			//Console.WriteLine("Hashing: {0:F3} s, {1:F2} MB/s", swh.Elapsed.TotalSeconds, dnr / swh.Elapsed.TotalSeconds);
			return 0;
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

			return CheckRepo(repoDir, updateUrl) ? 0 : 1;
		}
	}
}
