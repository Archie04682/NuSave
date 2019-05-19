namespace NuSave.Core
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Net;
  using System.Threading;
  using System.Xml.Linq;
  using Newtonsoft.Json;
  using NuGet;

  public class Downloader
  {
    private const string DefaultSource = "https://packages.nuget.org/api/v2";
    private readonly bool _allowPreRelease;
    private readonly bool _allowUnlisted;
    private readonly string _id;
    private readonly bool _json;
    private readonly string _outputDirectory;
    private readonly bool _silent;
    private readonly string _source;
    private readonly string _version;

    private IPackageRepository _repository;
    private List<IPackage> _toDownload = new List<IPackage>();

    public Downloader(
      string source,
      string outputDirectory,
      string id,
      string version,
      bool allowPreRelease = false,
      bool allowUnlisted = false,
      bool silent = false,
      bool json = false)
    {
      _source = source;
      _outputDirectory = outputDirectory;
      _id = id;
      _version = version;
      _allowPreRelease = allowPreRelease;
      _allowUnlisted = allowUnlisted;
      _json = json;
      _silent = _json ? true : silent;
    }

    private IPackageRepository Repository
    {
      get
      {
        if (_repository == null) _repository = PackageRepositoryFactory.Default.CreateRepository(GetSource());
        return _repository;
      }
    }

    public void Download()
    {
      if (!_silent)
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Downloading");
        Console.ResetColor();
      }

      var webClient = new WebClient();

      foreach (var package in _toDownload)
      {
        if (PackageExists(package.Id, package.Version.ToString())) continue;

        if (!_silent) Console.WriteLine($"{package.Id} {package.Version}");

        var dataServcePackage = (DataServicePackage) package;
        // We keep retrying forever until the user will press Ctrl-C
        // This lets the user decide when to stop retrying.
        // The reason for this is that building the dependencies list is expensive
        // on slow internet connection, when the CLI crashes because of a WebException
        // the user has to wait for the dependencies list to build rebuild again.
        while (true)
          try
          {
            var nugetPackageOutputPath = GetNuGetPackagePath(package.Id, package.Version.ToString());
            var downloadUrl = dataServcePackage.DownloadUrl;
            var proxy = webClient.Proxy;
            if (proxy != null)
            {
              var proxyuri = proxy.GetProxy(downloadUrl).ToString();
              webClient.UseDefaultCredentials = true;
              webClient.Proxy = new WebProxy(proxyuri, false) {Credentials = CredentialCache.DefaultCredentials};
            }

            webClient.DownloadFile(downloadUrl, nugetPackageOutputPath);
            break;
          }
          catch (WebException e)
          {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{e.Message}. Retrying in one second...");
            Console.ResetColor();
            Thread.Sleep(1000);
          }
      }
    }

    public void ResolveDependencies(string csprojPath = null)
    {
      if (_toDownload != null && _toDownload.Count > 1) _toDownload = new List<IPackage>();

      if (!_silent)
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Resolving dependencies");
        Console.ResetColor();
      }

      if (csprojPath == null)
      {
        IPackage package = string.IsNullOrWhiteSpace(_version)
          ? Repository.FindPackage(_id)
          : Repository.FindPackage(_id, SemanticVersion.Parse(_version), _allowPreRelease, _allowUnlisted);

        if (package == null) throw new Exception("Could not resolve package");

        ResolveDependencies(package);
      }
      else
      {
        XNamespace @namespace = "http://schemas.microsoft.com/developer/msbuild/2003";
        var csprojDoc = XDocument.Load(csprojPath);

        var references = csprojDoc
          .Element(@namespace + "Project")
          .Elements(@namespace + "ItemGroup")
          .Elements(@namespace + "PackageReference")
          .Select(e => new MsBuildPackageRef
          {
            Include = e.Attribute("Include").Value,
            Version = e.Element(@namespace + "Version").Value
          });
        ResolveDependencies(references);

        var dotnetCliToolReferences = csprojDoc
          .Element(@namespace + "Project")
          .Elements(@namespace + "ItemGroup")
          .Elements(@namespace + "DotNetCliToolReference")
          .Select(e => new MsBuildPackageRef
          {
            Include = e.Attribute("Include").Value,
            Version = e.Element(@namespace + "Version").Value
          });
        ResolveDependencies(dotnetCliToolReferences);
      }

      if (_json) Console.WriteLine(JsonConvert.SerializeObject(GetDependencies()));
    }

    private void ResolveDependencies(IEnumerable<MsBuildPackageRef> references)
    {
      foreach (var packageRef in references)
      {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{packageRef.Include} {packageRef.Version}");
        Console.ResetColor();

        ResolveDependencies(packageRef);
      }
    }

    private string GetNuGetPackagePath(string id, string version)
    {
      return Path.Combine(_outputDirectory, $"{id}.{version}.nupkg".ToLower());
    }

    private string GetNuGetHierarchialDirPath(string id, string version)
    {
      return Path.Combine(_outputDirectory, id.ToLower(), version);
    }

    private bool PackageExists(string id, string version)
    {
      var nugetPackagePath = GetNuGetPackagePath(id, version);
      if (File.Exists(nugetPackagePath)) return true;

      var nuGetHierarchialDirPath = GetNuGetHierarchialDirPath(id, version);
      if (Directory.Exists(nuGetHierarchialDirPath)) return true;

      return false;
    }

    private void ResolveDependencies(MsBuildPackageRef msBuildpackageRef)
    {
      IPackage nugetPackage = Repository.FindPackage(msBuildpackageRef.Include,
        SemanticVersion.Parse(msBuildpackageRef.Version), true, true);
      ResolveDependencies(nugetPackage);
    }

    private void ResolveDependencies(IPackage package)
    {
      if (PackageExists(package.Id, package.Version.ToString())) return;

      _toDownload.Add(package);

      foreach (var set in package.DependencySets)
      foreach (var dependency in set.Dependencies)
      {
        if (PackageExists(dependency.Id, dependency.VersionSpec.ToString())) continue;

        var found = Repository.FindPackage(
          dependency.Id,
          dependency.VersionSpec,
          _allowPreRelease,
          _allowUnlisted);

        if (found == null)
        {
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine($"Could not resolve dependency: {dependency.Id} {dependency.VersionSpec.ToString()}");
          Console.ResetColor();
        }
        else
        {
          if (!_toDownload.Any(p => p.Title == found.Title && p.Version == found.Version))
          {
            _toDownload.Add(found);
            if (!_silent) Console.WriteLine($"{found.Id} {found.Version}");
            ResolveDependencies(found);
          }
        }
      }
    }

    private string GetSource()
    {
      if (_source == null)
        return DefaultSource;
      return _source;
    }

    /// <summary>
    ///   Convenience method that can be used in powershell in combination with Out-GridView
    /// </summary>
    /// <returns></returns>
    public List<SimplifiedPackageInfo> GetDependencies()
    {
      var list = new List<SimplifiedPackageInfo>();
      foreach (var p in _toDownload)
        list.Add(new SimplifiedPackageInfo
        {
          Id = p.Id,
          Version = p.Version.ToString(),
          Authors = string.Join(" ", p.Authors)
        });
      return list;
    }
  }
}