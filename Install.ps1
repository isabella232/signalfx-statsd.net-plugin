param
(
	[Parameter()]
	[Hashtable] $settings = @{}
)

if(!$PSScriptRoot){ $PSScriptRoot = Split-Path $MyInvocation.MyCommand.Path -Parent }

Set-StrictMode -version Latest

function Test-IsAdmin
{
	$identity = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    If (-NOT $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    {
		throw 'You are not currently running this installation under an Administrator account.  Installation aborted!'
	}
}

function CheckPolicy
{
    $executionPolicy  = (Get-ExecutionPolicy)
    $executionRestricted = ($executionPolicy -eq "Restricted")
    if ($executionRestricted){
        throw @"
Your execution policy is $executionPolicy, this means you will not be able import or use any scripts including modules.
To fix this change you execution policy to something like RemoteSigned.

        PS> Set-ExecutionPolicy RemoteSigned

For more information execute:

        PS> Get-Help about_execution_policies

"@
    }
}


function Merge-Parameters()
{
	param
	(
		[Parameter(Mandatory=$true)]
		[Hashtable] $Hash
	)

	$defaults =
	@{
		SampleInterval = '5s';
		DefaultDimensions= @{}
		SourceValue= ''
		AwsIntegration=$false
	}

	$allowedKeys = ($defaults | Select -ExpandProperty Keys) + @('ApiToken', 'SourceType')
	$Hash | Select -ExpandProperty Keys |
	% {
		if (-not $allowedKeys -contains $_)
		{
			$msg = "Parameter $_ not expected"
			Write-Error -Message $msg -Category InvalidArgument
			throw $msg
		}
		$defaults.$_ = $Hash.$_
	}
	$defaults
}

function Install()
{
	$path = "${PSScriptRoot}\statsd.net";
	$shellApplication = New-Object -com Shell.Application

	$statsdNetItems = $shellApplication.NameSpace($path).Items();
	$extracted = "${Env:ProgramFiles}\statsd.net"
	if (!(test-path $extracted))
	{
		[Void](New-Item $extracted -type directory)
	}
	$shellApplication.NameSpace($extracted).CopyHere($statsdNetItems, 0x14)
}

function Modify-ConfigFile()
{

	param
	(

	    [parameter(Mandatory=$true)]
            [string]
	    $ApiToken,

	    [parameter(Mandatory=$true)]
	    [ValidateSet('netbios','dns','fqdn','custom')]
	    [string]
	    $SourceType,

	    [parameter(Mandatory=$false)]
	    [string]
	    $SourceValue= '',

	    [parameter(Mandatory=$true)]
	    [bool]
	    $AwsIntegration,

	    [parameter(Mandatory=$true)]
	        [AllowEmptyCollection()]
	    [Hashtable]
	    $DefaultDimensions,

	    [parameter(Mandatory=$true)]
	    [string]
	    $SampleInterval

	)

        if ($SourceType -eq "custom" -and $SourceValue -eq "")
	{
		throw "SourceValue must be specified if SourceType is 'custom'"
	}

	$path = "${Env:ProgramFiles}\statsd.net\statsdnet.config"
	$xml = New-Object Xml
	$xml.Load($path+".tmpl")

        # configure reporting to SignalFx
	$xml.statsdnet.backends.signalfx.SetAttribute("apiToken", $ApiToken)
	$xml.statsdnet.backends.signalfx.SetAttribute("sourceType", $SourceType)
	$xml.statsdnet.backends.signalfx.SetAttribute("sourceValue", $SourceValue)
	$xml.statsdnet.backends.signalfx.SetAttribute("maxTimeBetweenBatches", $SampleInterval)
	if ($AwsIntegration)
	{
		$xml.statsdnet.backends.signalfx.SetAttribute("awsIntegration", "true");
	}


	# configure performance counters to collection
	$defaultDimensionsNode = $xml.SelectSingleNode('//defaultDimensions')
	if ($defaultDimensionsNode -ne $null)
	{
                $defaultDimensionsNode.RemoveAll()
        }
	else
	{
	        $defaultDimensionsNode = $xml.CreateElement('defaultDimensions')
		[Void]$xml.statsdnet.backends.signalfx.AppendChild($defaultDimensionsNode)
	}

	$DefaultDimensions.GetEnumerator() | % {
	                  $defaultDimensionNode = $xml.CreateElement('defaultDimension')
			  $defaultDimensionNode.SetAttribute('name', $_.Key)
			  $defaultDimensionNode.SetAttribute('value', $_.Value)
			  [Void]$defaultDimensionsNode.AppendChild($defaultDimensionNode)
		}

	$xml.Save($path)
}

function Install-Service()
{
	[CmdletBinding()]
	param
	(

	    [parameter(Mandatory=$true)]
            [string]
	    $ApiToken,

	    [parameter(Mandatory=$true)]
	    [ValidateSet('netbios','dns','fqdn','custom')]
	    [string]
	    $SourceType,

	    [parameter(Mandatory=$false)]
	    [string]
	    $SourceValue= '',

	    [parameter(Mandatory=$false)]
	    [Hashtable]
	    $DefaultDimensions = @{},

	    [parameter(Mandatory=$false)]
	    [bool]
	    $AwsIntegration = $false,

	    [parameter(Mandatory=$false)]
	    [string]
	    $SampleInterval = '5s'

	)

	# install and start


	if ((Get-Service statsd.net -ErrorAction SilentlyContinue) -ne $null)
	{
		Stop-Service statsd.net
		$service = Get-WmiObject -Class Win32_Service -Filter "Name='statsd.net'"
		$service.delete()
	}

	Install
	& "${Env:ProgramFiles}\statsd.net\statsdnet.exe" --install
	Modify-ConfigFile -ApiToken $ApiToken -SourceType $SourceType `
	                  -DefaultDimensions $DefaultDimensions `
			  -AwsIntegration $AwsIntegration `
			  -SampleInterval $SampleInterval

	Start-Service statsd.net

}

Test-IsAdmin
CheckPolicy
$mergedSettings = Merge-Parameters -Hash $settings
Install-Service @mergedSettings
