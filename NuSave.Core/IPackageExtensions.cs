namespace NuSave.Core
{
  using NuGet;

  public static class IPackageExtensions
  {
    public static string GetFileName(this IPackage package)
    {
      return $"{package.Id}.{package.Version}.nupkg".ToLower();
    }
  }
}