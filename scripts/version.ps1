function Get-OpenRevoVersion {
    $properties = [xml](Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot) 'Directory.Build.props') -Raw)
    $version = [string]$properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must contain a three-part release version.' }
    return $version
}
