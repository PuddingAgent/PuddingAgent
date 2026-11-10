$ErrorActionPreference = 'Stop'
$directory = Get-Item -LiteralPath $args[0] -Force
if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The private state directory cannot be a reparse point.'
}
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$rule = [Security.AccessControl.FileSystemAccessRule]::new($owner, 'FullControl', $inherit, 'None', 'Allow')
$acl.AddAccessRule($rule)
$directory.SetAccessControl($acl)
