split-path $SCRIPT:MyInvocation.MyCommand.Path -parent
$Date = Get-Date -Format yyyymmdd_HHMM
Start-Transcript -Path "Logs\$Date.txt"
$DefaultVMSize = "Standard_F2s"
$DefaultVMDisk = "Premium_LRS"
$DefaultOSImage = "2019-Datacenter"
$DefaultOSWSImage = "19h2-ent"
$DefaultWVDImage = "20h1-evd-o365pp"

$AzureModule = Get-Module -ListAvailable -Name Az.*
$updatemodule = get-command Update-Module
$UpdateVer = $updatemodule.Version.ToString()

if($UpdateVer -le "2.2.4"){
    Write-Host "Update-Module Function needs to be updated"
    Install-Module -Name PowerShellGet -RequiredVersion 2.2.5 -Force
}

    if ($AzureModule.Name -notlike "Az.*"){
    Write-Host "Can't find Azure Module, installing module"
    Install-Module Az -Force -Verbose -Scope CurrentUser
    Import-Module Az
    }
    else
    {
    Write-Host "Found Azure Module"
    $StorageModule = Get-InstalledModule -Name Az.Storage
    $AccountModule = Get-InstalledModule -Name Az.Accounts
        if($StorageModule.Version -lt "3.0.0"){
        Write-Host "Updating Azure Storage Module"
        Update-Module -Name Az.Storage -Force -Scope CurrentUser -WarningAction Ignore
        Import-Module -Name Az.Storage -RequiredVersion 3.0.0
        #Import-Module Az -Scope Global
        }
        if($AccountModule.Version -lt "2.1.2"){
        Write-Host "Updating Azure Accounts Module"
        Update-Module -Name Az.Accounts -Force -Scope CurrentUser -WarningAction Ignore
        Import-Module -Name Az.Accounts -RequiredVersion 2.1.2
        #Import-Module Az -Scope Global
        }
    else
    {
    Write-Host "No Updates needed for Az Modules"
    #Import-Module Az -Scope Global
    }
}


############  LOGIN SECTION  #############


if (Get-AzContext) {
    Write-Host "We have connection, start building!!" -ForegroundColor Green
    Get-Azcontext |fl
    $Title = "Task Menu"
$Caption = @"

1 - Continue with current login
2 - Reconnect with different account Commercial Azure Account
3 - Reconnect with different account Gov't Azure Account
Q - Quit
 
Select a choice:
"@
 
$coll = @()
#$coll = New-Object System.Collections.ObjectModel.Collection
 
$a = [System.Management.Automation.Host.ChoiceDescription]::new("&1 Current login")
$a | Add-Member -MemberType ScriptMethod -Name Invoke -Value {Write-Host "Good Luck with your lab!!" -ForegroundColor Green ; Return} -force
$a.HelpMessage = "Continue with IaaS GUI"
$coll+=$a
 
$b = [System.Management.Automation.Host.ChoiceDescription]::new("&2 Reconnect Commercial")
$b.HelpMessage = "Get top processes"
$b | Add-Member -MemberType ScriptMethod -Name Invoke -Value {Connect-AzAccount -Force -Verbose ; Return} -force
$coll+=$b
 
$c = [System.Management.Automation.Host.ChoiceDescription]::new("&3 Reconnect Gov't")
$c.HelpMessage = "Get disk information"
$c | Add-Member -MemberType ScriptMethod -Name Invoke -Value {Connect-AzAccount -Force -Environment AzureUSGovernment -Verbose ; Return} -force
$coll+=$c
 
$q = [System.Management.Automation.Host.ChoiceDescription]::new("&Quit")
$q | Add-Member -MemberType ScriptMethod -Name Invoke -Value {Write-Host "Have a nice day." -ForegroundColor Green | Exit-PSSession} -force
$q.HelpMessage = "Quit and exit"
$coll+=$q
 

$r = $host.ui.PromptForChoice($Title,$Caption,$coll,0)
$coll[$r].invoke() | Out-Host

  }
  else
  {
  Write-Host "No connection to Azure, Please login" -ForegroundColor Yellow
      $Title = "Task Menu"
$Caption = @"
1 - Connect to Commercial Azure Account
2 - Connect to Gov't Azure Accountt
Q - Quit
 
Select a choice:
"@
 
$coll = @()
#$coll = New-Object System.Collections.ObjectModel.Collection
 
$a = [System.Management.Automation.Host.ChoiceDescription]::new("&1 Login Azure")
$a | Add-Member -MemberType ScriptMethod -Name Invoke -Value {Connect-AzAccount -Verbose ; Return} -force
$a.HelpMessage = "Continue with IaaS GUI"
$coll+=$a
 
$b = [System.Management.Automation.Host.ChoiceDescription]::new("&2 Login US Gov't Azure")
$b.HelpMessage = "Get top processes"
$b | Add-Member -MemberType ScriptMethod -Name Invoke -Value {Connect-AzAccount -Environment AzureUSGovernment -Verbose ; Return} -force
$coll+=$b
 
$q = [System.Management.Automation.Host.ChoiceDescription]::new("&Quit")
$q | Add-Member -MemberType ScriptMethod -Name Invoke -Value {Write-Host "Have a nice day." -ForegroundColor Green | Exit-PSSession} -force
$q.HelpMessage = "Quit and exit"
$coll+=$q
 

$r = $host.ui.PromptForChoice($Title,$Caption,$coll,0)
$coll[$r].invoke() | Out-Host

  }

if ($coll[$r].Label -eq "&Quit"){
exit
}
############  END OF LOGIN ###############



# Add required assemblies
Add-Type -AssemblyName PresentationFramework, System.Drawing, System.Windows.Forms, WindowsFormsIntegration
[Windows.Forms.Application]::EnableVisualStyles()

#Push-Location (Split-Path $MyInvocation.MyCommand.Path)

Clear-Host

$inputxml = Get-Content -Path .\form.xml

$inputXML = $inputXML -replace 'mc:Ignorable="d"','' -replace "x:N",'N' -replace '^<Win.*', '<Window'
[void][System.Reflection.Assembly]::LoadWithPartialName('presentationframework')
[xml]$XAML = $inputXML
#Read XAML

#$syncHash = [hashtable]::Synchronized(@{})
$reader=(New-Object System.Xml.XmlNodeReader $xaml)

try{
    $Form=[Windows.Markup.XamlReader]::Load( $reader )
    }
catch{
    Write-Warning "Unable to parse XML, with error: $($Error[0])`n Ensure that there are NO SelectionChanged or TextChanged properties (PowerShell cannot process them)"
    throw
    }

#===========================================================================
# Load XAML Objects In PowerShell
#===========================================================================
  
$xaml.SelectNodes("//*[@Name]") | ForEach-Object {
    try {Set-Variable -Name "WPF$($_.Name)" -Value $Form.FindName($_.Name) -ErrorAction Stop}
    catch{throw}
    }
 
Function Get-FormVariables{
if ($global:ReadmeDisplay -ne $true){$global:ReadmeDisplay=$true}
#write-host "Found the following interactable elements from our form" -ForegroundColor Cyan
get-variable WPF*
}
 
$formvar = Get-FormVariables


#===========================================================================
# Use this space to add code to the various form elements in your GUI
#===========================================================================

#  Azure API Testing
#$ResHeaders = @{'authorization' = $authenticationResult.CreateAuthorizationHeader()}
#$header = @{Authorization = 'Bearer' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$($con.TokenCache.CacheData)")) }
#$header = @{Authorization = 'Bearer' + $con2.Context.TokenCache.CacheData}
#$Header = @{Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$($AzureDevOpsPAT)")) }

#HyperLink
$WPFgithub.Add_MouseLeftButtonUp({[system.Diagnostics.Process]::start('https://github.com/chlaplan/IaaS-Builder')})
$WPFgithub.Add_MouseEnter({$WPFgithub.Foreground = 'Purple'})
$WPFgithub.Add_MouseLeave({$WPFgithub.Foreground = 'DarkBlue'})

# Add current Azure connection
  if (Get-AzContext) {
    Write-Host "We have connection, start building!!" -ForegroundColor Green
    $Sub = Get-AzSubscription | select "Name"
    foreach($Subs in $Sub){
        $WPFSubscription1.AddChild($Subs.Name)
        }
  }
  else
  {
  Write-Host "No connection to Azure, Please login" -ForegroundColor Yellow
  }

$Locations = Get-AzLocation
# Build Location List
    $WPFSubscription1.Add_DropDownClosed({
    $WPFLocations1.Items.Clear()
    foreach($Location in $Locations){
        $WPFLocations1.AddChild($Location.Location)
        }
    })

# Get WVD Locations
$WVDLocations = Get-AzLocation | where providers -EQ Microsoft.DesktopVirtualization

foreach ($WVDLocation in $WVDLocations){
$WPFWVD_Metadata.Addchild($WVDLocation.Location)
}

# https://docs.microsoft.com/en-us/azure/virtual-machines/windows/faq#what-are-the-password-requirements-when-creating-a-vm
$WPFadminpassword1.Add_LostFocus({
    if(($WPFadminpassword1.Password -cmatch '[a-z]') -and ($WPFadminpassword1.Password -cmatch '[A-Z]') -and ($WPFadminpassword1.Password -match '\d') -and ($WPFadminpassword1.Password.length -ge 8) -and ($WPFadminpassword1.Password.length -le 64) -and ($WPFadminpassword1.Password -match '!|@|#|%|^|&|$') -and ($WPFadminpassword1.Password -notmatch 'abc@123|iloveyou!|P@$$w0rd|P@ssw0rd|P@ssword123|Pa$$word|pass@word1|Password!|Password1|Password22'))
    {
        Write-Host "Admin Password meets complexity"  -ForegroundColor Green
        $WPFPW.Foreground = "#FF068113" #Green
        $WPFPW.Text = "Password meets complexity"
        $WPFadminpassword1.BorderBrush = "#FF068113"
    }
    else
    {
        Write-Host "Admin Password does not meet complexity"  -ForegroundColor Yellow
        $WPFPW.Foreground = "#FFF21802" #Red
        $WPFPW.Text = "Password does not meet complexity"
        $WPFadminpassword1.BorderBrush = '#FFF21802'
    }
})


$WPFLocations1.Add_SelectionChanged({
    $Location = $WPFLocations1.SelectedItem
    Write-Host "Building Variables " -ForegroundColor Green
    $vmsize = Get-AzVMSize -Location $WPFLocations1.SelectedItem | Where {$_.statuscode -eq "OK"}
    $SACAvmsize = $vmsize | Where-Object NumberofCores -GE "4"
    $SQLoffers = Get-AzVMImageOffer -Location $WPFLocations1.SelectedItem -PublisherName "MicrosoftSQLServer" | Select offer
    $serverskus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -Offer "WindowsServer" -PublisherName "MicrosoftWindowsServer" | Select Skus    
    $clientskus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -Offer "Windows-10" -PublisherName "MicrosoftWindowsDesktop" | Select Skus
    $client365skus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -Offer "Office-365" -PublisherName "MicrosoftWindowsDesktop" | Select Skus
    $sharePointSkus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -PublisherName MicrosoftSharePoint -Offer MicrosoftSharePointServer
    #$F5Offers = Get-AzVMImageOffer -Location $WPFLocations1.SelectedItem -PublisherName "f5-networks"
    #$F5SKUS = Get-AzVMImageSku -Location 'USDoD East' -PublisherName "f5-networks" -Offer "f5-big-ip-byol"

    Write-Host "Adding Defaults" -ForegroundColor Green
    $WPFserver1disk.AddChild($DefaultVMDisk)
    $WPFadfsdisk.AddChild($DefaultVMDisk)
    $WPFexdisk.AddChild($DefaultVMDisk)
    $WPFsccm_ps_disk.AddChild($DefaultVMDisk)
    $WPFsccm_mpdp_disk.AddChild($DefaultVMDisk)
    $WPFsharepoint_disk.AddChild($DefaultVMDisk)
    $WPFSQLDisk.AddChild($DefaultVMDisk)
    $WPFserver5disk.AddChild($DefaultVMDisk)
    $WPFworkstationdisk.AddChild($DefaultVMDisk)
    $WPFWVD_Disk.AddChild($DefaultVMDisk)
    
    #############Load VMSize and Select Default#############
    Write-Host "Loading VMsizes and Disk information" -ForegroundColor Green

    $WPFserver1vmsize.items.Clear()
    $WPFadfssize.items.Clear()
    $WPFexsize.items.Clear()
    $WPFsscm_ps_size.items.Clear()
    $WPFsccm_mpdp_size.items.Clear()
    $WPFsharepoint_size.items.Clear()
    $WPFSQLsize.items.Clear()
    $WPFserver5size.items.Clear()
    $WPFworkstationsize.items.Clear()
    $WPFWVD_Size.items.Clear()
    $WPFsacaBIGIP1vmsize.items.Clear()
    $WPFsacaBIGIP2vmsize.items.Clear()
    $WPFsacaBIGIP3vmsize.items.Clear()
    $WPFsacaBIGIP4vmsize.items.Clear()
    $WPFsacaFWIPS1vmsize.items.Clear()
    $WPFsacaFWIPS2vmsize.items.Clear()
        foreach ($Size in $vmsize)
            {
                $WPFserver1vmsize.AddChild($size.Name)
                $WPFadfssize.AddChild($size.Name)
                $WPFexsize.AddChild($size.Name)
                $WPFsscm_ps_size.AddChild($size.Name)
                $WPFsccm_mpdp_size.AddChild($size.Name)
                $WPFsharepoint_size.AddChild($size.Name)
                $WPFSQLsize.AddChild($size.Name)
                $WPFserver5size.AddChild($size.Name)
                $WPFworkstationsize.AddChild($size.Name)
                $WPFWVD_Size.AddChild($size.Name)
            }
         foreach ($Size in $SACAvmsize)
            {
                $WPFsacaBIGIP1vmsize.AddChild($size.Name)
                $WPFsacaBIGIP2vmsize.AddChild($size.Name)
                $WPFsacaBIGIP3vmsize.AddChild($size.Name)
                $WPFsacaBIGIP4vmsize.AddChild($size.Name)
                $WPFsacaFWIPS1vmsize.AddChild($size.Name)
                $WPFsacaFWIPS2vmsize.AddChild($size.Name)
                $WPFsacaLinuxJBvmsize.AddChild($size.Name)
                $WPFsacaWinJBvmsize.AddChild($size.Name)
            }
    
    Write-Host "Setting Default Size and Disk" -ForegroundColor Green
    $WPFserver1vmsize.SelectedItem = $DefaultVMSize
    $WPFserver1disk.SelectedItem = $DefaultVMDisk
    $WPFadfssize.SelectedItem = $DefaultVMSize
    $WPFadfsdisk.SelectedItem = $DefaultVMDisk
    $WPFexsize.SelectedItem = "Standard_F4s"
    $WPFexdisk.SelectedItem = $DefaultVMDisk
    $WPFsscm_ps_size.SelectedItem = $DefaultVMSize
    $WPFsccm_ps_disk.SelectedItem = $DefaultVMDisk   
    $WPFsccm_mpdp_size.SelectedItem = $DefaultVMSize
    $WPFsccm_mpdp_disk.SelectedItem = $DefaultVMDisk
    $WPFsharepoint_size.SelectedItem = "Standard_F4s"
    $WPFsharepoint_disk.SelectedItem = $DefaultVMDisk    
    $WPFSQLsize.SelectedItem = $DefaultVMSize
    $WPFSQLDisk.SelectedItem = $DefaultVMDisk
    $WPFserver5size.SelectedItem = $DefaultVMSize
    $WPFserver5disk.SelectedItem = $DefaultVMDisk    
    $WPFworkstationsize.SelectedItem = $DefaultVMSize
    $WPFworkstationdisk.SelectedItem = $DefaultVMDisk 
    $WPFWVD_Size.SelectedItem = $DefaultVMSize
    $WPFWVD_Disk.SelectedItem = $DefaultVMDisk
    $WPFsacaBIGIP1vmsize.SelectedItem = "Standard_F4s"
    $WPFsacaBIGIP2vmsize.SelectedItem = "Standard_F4s"
    $WPFsacaBIGIP3vmsize.SelectedItem = "Standard_F4s"
    $WPFsacaBIGIP4vmsize.SelectedItem = "Standard_F4s"
    $WPFsacaFWIPS1vmsize.SelectedItem = "Standard_F4s"
    $WPFsacaFWIPS2vmsize.SelectedItem = "Standard_F4s"
    $WPFsacaLinuxJBvmsize.SelectedItem = "Standard_F4s"
    $WPFsacaWinJBvmsize.SelectedItem = "Standard_F4s"
    
    #Load Images and Select Default
    Write-Host "Loading Images and SKUs" -ForegroundColor Green

    $WPFserver1image.Items.Clear()
    $WPFADFSimage.Items.Clear()
    $WPFeximage.Items.Clear()
    $WPFsccmdpimage.Items.Clear()
    $WPFserver5image.Items.Clear()
        foreach ($serversku in $serverskus){
            $WPFserver1image.AddChild($serversku.skus)
            $WPFADFSimage.AddChild($serversku.skus)
            $WPFeximage.AddChild($serversku.skus)
            $WPFsccmdpimage.AddChild($serversku.skus)
            $WPFserver5image.AddChild($serversku.skus)
        }
    $WPFserver1image.SelectedItem = "$DefaultOSImage"
    $WPFADFSimage.SelectedItem = "$DefaultOSImage"
    $WPFeximage.SelectedItem = "2016-Datacenter"
    $WPFsccmdpimage.SelectedItem = "$DefaultOSImage"
    $WPFserver5image.SelectedItem = "$DefaultOSImage"


        foreach ($SQLoffer in $SQLoffers){
    $WPFsccmimageoffer.AddChild($SQLoffer.offer)
    }
    $WPFsccmimageoffer.SelectedItem = "sql2019-ws2019"
    
    $SQLSkus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -Offer $WPFsccmimageoffer.SelectedItem -PublisherName "MicrosoftSQLServer" | Select Skus

    $WPFsccmimagesku.Items.Clear()
        foreach ($SQLsku in $SQLskus){
    $WPFsccmimagesku.AddChild($SQLsku.skus)
    }
    $WPFsccmimagesku.SelectedItem = "standard"

    $WPFsharepointimage.Items.Clear()
        foreach ($sharePointSku in $sharePointSkus){
    $WPFsharepointimage.AddChild($sharePointSku.skus)
    }
    $WPFsharepointimage.SelectedItem = "sp2019"

    $WPFSQLImage.Items.Clear()
        foreach ($SQLoffer in $SQLoffers){
    $WPFSQLImage.AddChild($SQLoffer.offer)
    }
    $WPFSQLImage.SelectedItem = "sql2019-ws2019"

    $WPFSQLsku.Items.Clear()
        foreach ($SQLsku in $SQLskus){
    $WPFSQLsku.AddChild($SQLsku.skus)
    }
    $WPFSQLsku.SelectedItem = "standard"

    $WPFworkstationimage.Items.Clear()
    foreach ($clientsku in $clientskus){
    $WPFworkstationimage.AddChild($clientsku.skus)
    }
    $WPFworkstationimage.SelectedItem = "$DefaultOSWSImage"

    $WPFWVD_Image.Items.Clear()
    foreach ($clientsku in $client365skus){
    $WPFWVD_Image.AddChild($clientsku.skus)
    }
    $WPFWVD_Image.SelectedItem = $DefaultWVDImage

    # Query SACA DNS Label availability 
    $CheckDNS = Test-AzDnsAvailability -DomainNameLabel $WPFSACA_DNS.Text -Location $WPFLocations1.SelectedItem
    If($CheckDNS -eq $false){
    Write-host "SACA DNS Name Not Available" -ForegroundColor Yellow
    $WPFSACA_label.Foreground = "#FFF21802" #Red
    $WPFSACA_label.Text = "Not Available"
    $WPFSACA_DNS.BorderBrush = '#FFF21802'
    }
    else
    {
    Write-Host "DNS Label Name Available" -ForegroundColor Green
    $WPFSACA_label.Foreground = "#FF068113" #Green
    $WPFSACA_label.Text = "Available"
    $WPFSACA_DNS.BorderBrush = '#FF068113'
    }
    
    # Query Usage
    $NetUsage = Get-AznetworkUsage -Location $WPFLocations1.SelectedItem | Where-Object {$_.CurrentValue -gt 0} | Format-Table ResourceType, CurrentValue, Limit
    $VMUsage = Get-AzvmUsage -Location $WPFLocations1.SelectedItem | Where-Object {$_.CurrentValue -gt 0}
    $StorageUsage = Get-AzStorageUsage -Location $WPFLocations1.SelectedItem | Where-Object {$_.CurrentValue -gt 0}

    if($NetUsage.Count -gt 0){
    Write-Host "Network Usage" -ForegroundColor Yellow
    $NetUsage | Format-Table | Out-String|% {Write-Host $_}
    $WPFusage2.Text += $NetUsage | Format-Table | Out-String
    }

    if($VMUsage.Count -gt 0){
    Write-Host "VM Usage" -ForegroundColor Yellow
    $VMUsage | Format-Table | Out-String|% {Write-Host $_}
    $WPFusage2.Text += $VMUsage | Format-Table | Out-String
    }

    if($StorageUsage.Count -gt 0){
    Write-Host "Storage Usage" -ForegroundColor Yellow
    $StorageUsage | Format-Table | Out-String|% {Write-Host $_}
    $WPFusage2.Text += $StorageUsage | Format-Table | Out-String
    }
})

#End Load Images and Select Default

# Query StorageAccount Name
   $WPFsaname1.Add_LostFocus({
    $CheckSA = Get-AzStorageAccountNameAvailability -Name $WPFsaname1.Text
        If($CheckSA.NameAvailable -eq $false){
        Write-host "SA Name Not Available" -ForegroundColor Yellow
        $WPFSA.Foreground = "#FFF21802" #Red
        $WPFSA.Text = "Not Available"
        $WPFsaname1.BorderBrush = '#FFF21802'
        }
        else
        {
        Write-Host "SA Name Available" -ForegroundColor Green
        $WPFSA.Foreground = "#FF068113" #Green
        $WPFSA.Text = "Available"
        $WPFsaname1.BorderBrush = '#FF068113'
        }
   })
    
    $WPFsaname1.Add_Loaded({
    $CheckSA = Get-AzStorageAccountNameAvailability -Name $WPFsaname1.Text
    If($CheckSA.NameAvailable -eq $false){
    Write-host "SA Name Not Available" -ForegroundColor Yellow
    $WPFSA.Foreground = "#FFF21802" #Red
    $WPFSA.Text = "Not Available"
    $WPFsaname1.BorderBrush = '#FFF21802'
    }
    else
    {
    Write-Host "SA Name Available" -ForegroundColor Green
    $WPFSA.Foreground = "#FF068113" #Green
    $WPFSA.Text = "Available"
    $WPFsaname1.BorderBrush = '#FF068113'
    }
    })
     
# End StorageAccount Name

# Query SACA DNS Label availability
    $WPFSACA_DNS.Add_LostFocus({
    $CheckDNS = Test-AzDnsAvailability -DomainNameLabel $WPFSACA_DNS.Text -Location $WPFLocations1.SelectedItem
    If($CheckDNS -eq $false){
    Write-host "SACA DNS Name Not Available" -ForegroundColor Yellow
    $WPFSACA_label.Foreground = "#FFF21802" #Red
    $WPFSACA_label.Text = "Not Available"
    $WPFSACA_DNS.BorderBrush = '#FFF21802'
    }
    else
    {
    Write-Host "DNS Label Name Available" -ForegroundColor Green
    $WPFSACA_label.Foreground = "#FF068113" #Green
    $WPFSACA_label.Text = "Available"
    $WPFSACA_DNS.BorderBrush = '#FF068113'
    }
    })

#Query Disk type Premium_LRS or Standard_LRS
    $WPFserver1vmsize.Add_DropDownClosed({
    $WPFserver1disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFserver1vmsize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFserver1disk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFserver1disk.AddChild("Standard_LRS")
        }
    })

    $WPFadfssize.Add_DropDownClosed({
    $WPFadfsdisk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFadfssize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFadfsdisk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFadfsdisk.AddChild("Standard_LRS")
        }
    })

    $WPFexsize.Add_DropDownClosed({
    $WPFexdisk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFexsize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFexdisk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFexdisk.AddChild("Standard_LRS")
        }
    })

    $WPFsscm_ps_size.Add_DropDownClosed({
    $WPFsccm_ps_disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFsscm_ps_size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFsccm_ps_disk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFsccm_ps_disk.AddChild("Standard_LRS")
        }
    })

    $WPFsccm_mpdp_size.Add_DropDownClosed({
    $WPFsccm_mpdp_disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFsccm_mpdp_size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFsccm_mpdp_disk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFsccm_mpdp_disk.AddChild("Standard_LRS")
        }
    })

    $WPFsharepoint_size.Add_DropDownClosed({
    $WPFsharepoint_disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFsharepoint_size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFsharepoint_disk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFsharepoint_disk.AddChild("Standard_LRS")
        }
    })

    $WPFSQLSize.Add_DropDownClosed({
    $WPFSQLDisk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFSQLSize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFSQLDisk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFSQLDisk.AddChild("Standard_LRS")
        }
    })

    $WPFserver5size.Add_DropDownClosed({
    $WPFserver5disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFserver5size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFserver5disk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFserver5disk.AddChild("Standard_LRS")
        }
    })

    $WPFworkstationsize.Add_DropDownClosed({
    $WPFworkstationdisk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFworkstationsize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFworkstationdisk.AddChild("Premium_LRS")
        }
        else
        {
        $WPFworkstationdisk.AddChild("Standard_LRS")
        }
    })

    $WPFWVD_Size.Add_DropDownClosed({
    $WPFWVD_Disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFWVD_Size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities
        if($diskinfo[7].Value -eq $True){
        $WPFWVD_Disk.AddChild("Premium_LRS")
        #$WPFWVD_Disk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFWVD_Disk.AddChild("Standard_LRS")
        #$WPFWVD_Disk.SelectedItem = "Standard_LRS"
        }
    })

#END Query Disk type Premium_LRS or Standard_LRS

#####  SCCM Image offer query   #####
    $WPFsccmimageoffer.Add_SelectionChanged({
    $WPFsccmimagesku.Items.Clear()

    $SQLSkus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -Offer $WPFsccmimageoffer.SelectedItem -PublisherName "MicrosoftSQLServer" | Select Skus


        foreach ($SQLsku in $SQLskus){
        $WPFsccmimagesku.AddChild($SQLsku.skus)
        $WPFsccmimagesku.SelectedItem = "standard"
        }

    })




$WPFBuild1.Add_Click({

    #-------------------------------------------------------------------------------------------------------------------
    #Set variables
    $rg = $WPFresourcegroup1.Text
    $Sub = $WPFSubscription1.SelectedItem
    $saname = $WPFsaname1.Text
    $adminAccount = $WPFadminaccount1.Text
    $AdminPassword = $WPFadminpassword1.SecurePassword
    $AzureLocation = $WPFLocations1.SelectedItem
    $storagetype = 'Standard_GRS' # Other Options: 'Standard_GRS' , 'Standard_RAGRS' , 'Standard_ZRS' and 'Premium_LRS'
    $DomainName = $WPFDname1.Text
    $Prefix = $WPFprefix1.Text  # All computers will start with prefix
    $DCName = $WPFprefix1.Text+'dc01'
    $VMSize = $WPFVMSize.SelectedItem
    $addressubnet = $WPFaddresssubnet1.text
    $addressprefix = $WPFaddressprefix1.text
    $subnetname = $WPFsubnetName1.text
    $bastionsubnet = $WPFbastionsubnet.text
    $TokenExpireDate = $((get-date).ToUniversalTime().AddDays(1).ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ'))
    $UserFQDN = $WPFadminaccount1.Text + "@" + $WPFDname1.Text
    $VirtualNetworkName = $WPFprefix1.Text + "-vnet"
    $NSG = $WPFprefix1.Text + "-nsg"
    $NSGSACA = $WPFSACA_DNS.Text + "-mgmt-nsg"
    $VMTemplate = ".\Templates\AzureTemplate.json"
    
    if ($WPFSACA.IsChecked -eq $true -and $WPFSACA_Tier.SelectionBoxItem -eq "SACA 1 Tier"){
    $addressubnet = $WPFSACA_VDMS_Subnet.Text
    $VirtualNetworkName = $WPFSACA_VNET_Name.Text
    $subnetname = $WPFSACA_VDMS_Name.Text
    $NSG = $NSGSACA
    $VMTemplate = ".\Templates\AzureTemplateSACA.json"
    }
        if ($WPFSACA.IsChecked -eq $true -and $WPFSACA_Tier.SelectionBoxItem -eq "SACA 3 Tier"){
    $addressubnet = $WPFSACA_VDMS_Subnet.Text
    $VirtualNetworkName = $WPFSACA_VNET_Name.Text
    $subnetname = $WPFSACA_VDMS_Name.Text
    $NSG = $NSGSACA
    $VMTemplate = ".\Templates\AzureTemplateSACA.json"
    }
    
    #-------------------------------------------------------------------------------------------------------------------
    # Grab Custom Image ResourceID for Windows 10 images and feed it into JSON
    #$ImageID = Find-AzureRmResource | Where 'ResourceType' -eq 'Microsoft.Compute/images'

    #
    Select-AzSubscription -Subscription $WPFSubscription1.SelectedItem
    #-------------------------------------------------------------------------------------------------------------------
    # Creating new Resource Group
    $Getrg = Get-AzResourceGroup -Verbose

      if ($Getrg.ResourceGroupName -eq $rg) {
        $newrg = Get-AzResourceGroup -Name $rg -Verbose
        write-host "Resource group already exist" -ForegroundColor Green
      }
      else
      {
        $newrg = New-AzResourceGroup -Name $rg -Location $AzureLocation -Force -Verbose
        write-host "Created new resource group" -ForegroundColor Green
      }


    #-------------------------------------------------------------------------------------------------------------------
    # Creating new storage Account to upload files
    $GetSA = Get-AzStorageAccount -Verbose
  
      if ($GetSA.StorageAccountName -eq $saname) {
      write-host "Storage Account already exist" -ForegroundColor Green
        $storageaccount = Get-AzStorageAccount -ResourceGroupName $rg -Name $saname -Verbose
      }
      else
      {
      $storageaccount = New-AzStorageAccount -ResourceGroupName $rg -Name $saname -Location $AzureLocation -SkuName $storagetype -Verbose
      write-host "Creating new storage account for DSC files" -ForegroundColor Green
      }

    #-------------------------------------------------------------------------------------------------------------------
    # Creating new File Share to upload files and scripts
      $GetFS = Get-AzStorageShare -Context $storageaccount.Context -Verbose
  
      if ($GetFS.Name -eq "dscstatus") {
      $fs = Get-AzStorageShare -Name "dscstatus" -Context $storageaccount.Context
      }
      else
      {
      $fs = New-AzStorageShare -Name "dscstatus" -Context $storageaccount.Context
      }


    #-------------------------------------------------------------------------------------------------------------------

    # Creating storage containers
    $GetSC = Get-AzStorageContainer -Context $storageaccount.Context -Verbose
  
      if ($GetSC.Name -eq "dsc") {
      write-host "Storage container already exist" -ForegroundColor Green
      $dsccontainer = Get-AzStorageContainer -Name dsc -Context $storageaccount.Context -Verbose
      }
      else
      {
      write-host "Creating new storage container for DSC files" -ForegroundColor Green
      $dsccontainer = New-AzStorageContainer -Name dsc -Permission Blob -Context $storageaccount.Context -Verbose
      }


            ## Copying DSC to Azure Storage
            write-host "Uploading DSC to Azure Storage Container" -ForegroundColor Green
            Set-AzStorageBlobContent -Container $dsccontainer.Name -File .\DSC\Configuration.zip -Blob 'Configuration.zip' -Context $dsccontainer.Context -Force -Verbose -AsJob 
            Get-AzStorageContainer -Name $dsccontainer.Name -Context $dsccontainer.Context -Verbose
        
            write-host "Sleeping for 60secs so the DSC files can upload" -ForegroundColor Green
            Start-Sleep -Seconds 60
            $DSCs = Get-AzStorageBlob -Container dsc -Context $dsccontainer.Context -Verbose
        
            # Get uri DSC for Deployment
            $assetLocation = (Get-AzStorageBlob -blob 'Configuration.zip' -Container 'dsc' -Context $dsccontainer.Context).context.BlobEndPoint
            #$blobURL = $assetLocation -replace "https://" -split "/"
            #[string]$blobURL = $blobURL -replace $saname+"."
            Write-Host $assetLocation -ForegroundColor Green

    #####################################################################################################
    #Common variables
    $commonVariables = @{
    ResourceGroupName = $rg;
    prefix = $Prefix;
    DomainName = $DomainName;
    adminUsername = $adminAccount;
    adminPassword = $AdminPassword;
    _artifactsLocation = $assetLocation;
    addressprefix = $addressprefix;
    addresssubnet = $addressubnet;
    VirtualNetworkName = $VirtualNetworkName;
    NSG = $NSG;
    subnetname = $subnetname;
    DCName = $WPFServer1Name.Text;
    DCip = $WPFserver1IP.Text;
    DPMPName = $WPFsccm_dp_name.Text;
    PSName = $WPFsccm_ps_name.Text;
    STIG = $WPFSTIGs.IsChecked;
    MSFTBaseline = $WPFMSFTBaseline.IsChecked;
    sharePointVersion = $WPFsharepointimage.SelectedItem;
    SQLName = $WPFSQLName.Text
    BastionSubnet = $bastionsubnet
    }
    #####################################################################################################    
    # Virtual Networking Setup
    #
    if ($WPFSACA.IsChecked -eq $false){
    Write-Host "Building Virtual Network" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -TemplateFile .\Templates\Networking.json `
                                       -Name "Networking" `
                                       -vmsize $WPFserver1vmsize.SelectedItem `
                                       -vmdisk $WPFserver1disk.SelectedItem `
                                       -publisher "MicrosoftWindowsServer" `
                                       -offer "WindowsServer" `
                                       -sku $WPFserver1image.SelectedItem `
                                       -servername $WPFServer1Name.Text `
                                       -ip $WPFserver1IP.Text `
                                       -role $WPFServer1Role.Text `
                                       -Verbose
    }
    else
    {
    Write-Host "Skipping Normal Network Build, using SACA template" -ForegroundColor Yellow
    }
    #####################################################################################################    
    # 3 Tier SACA Networking
    #
    if ($WPFSACA.IsChecked -eq $true -and $WPFSACA_Tier.SelectionBoxItem -eq "SACA 3 Tier"){
    Write-Host "Building SACA Virtual Network" -ForegroundColor Green

    New-AzResourceGroupDeployment      -TemplateFile .\Templates\SACA\3T_SACA_NetworkBuild.json `
                                       -ResourceGroupName $rg `
                                       -Name "SACA_Networking" `
                                       -VNetName $WPFSACA_VNET_Name.Text `
                                       -DNSLabel $WPFSACA_DNS.Text `
                                       -Location $WPFLocations1.SelectedItem `
                                       -Subnet_Management_Name $WPFSACA_MGT_Name.Text `
                                       -Subnet_Management $WPFSACA_MGT_Subnet.Text `
                                       -Subnet_External_Name $WPFSACA_EXT_Name.Text `
                                       -Subnet_External $WPFSACA_Ext_Subnet.Text `
                                       -Subnet_External2_Name $WPFSACA_EXT2_Name.Text `
                                       -Subnet_External2 $WPFSACA_Ext2_Subnet.Text `
                                       -Subnet_InternalN_Name $WPFSACA_INTN_Name.Text `
                                       -Subnet_InternalN $WPFSACA_INTN_Subnet.Text `
                                       -Subnet_InternalS_Name $WPFSACA_INTS_Name.Text `
                                       -Subnet_InternalS $WPFSACA_INTS_Subnet.Text `
                                       -Subnet_IPSInt_Name $WPFSACA_IPSInt_Name.Text `
                                       -Subnet_IPSInt $WPFSACA_IPSInt_Subnet.Text `
                                       -Subnet_IPSExt_Name $WPFSACA_IPSExt_Name.Text `
                                       -Subnet_IPSExt $WPFSACA_IPSExt_Subnet.Text `
                                       -Subnet_VDMS_Name $WPFSACA_VDMS_Name.Text `
                                       -Subnet_VDMS $WPFSACA_VDMS_Subnet.Text `
                                       -Verbose
    }
    else
    {
    Write-Host "Skipping 3 Tier SACA Networking Build" -ForegroundColor Yellow
    }
    #####################################################################################################    
    # 1 Tier SACA Networking
    #
    if ($WPFSACA.IsChecked -eq $true -and $WPFSACA_Tier.SelectionBoxItem -eq "SACA 1 Tier"){
    Write-Host "Building SACA Virtual Network" -ForegroundColor Green

    New-AzResourceGroupDeployment      -TemplateFile .\Templates\SACA\1T_SACA_NetworkBuild.json `
                                       -ResourceGroupName $rg `
                                       -Name "SACA_Networking" `
                                       -VNetName $WPFSACA_VNET_Name.Text `
                                       -DNSLabel $WPFSACA_DNS.Text `
                                       -Location $WPFLocations1.SelectedItem `
                                       -SB_LB_IP $WPFSACA_SBLB_IP.Text `
                                       -Subnet_Management_Name $WPFSACA_MGT_Name.Text `
                                       -Subnet_Management $WPFSACA_MGT_Subnet.Text `
                                       -Subnet_External_Name $WPFSACA_EXT_Name.Text `
                                       -Subnet_External $WPFSACA_Ext_Subnet.Text `
                                       -Subnet_InternalS_Name $WPFSACA_INTS_Name.Text `
                                       -Subnet_InternalS $WPFSACA_INTS_Subnet.Text `
                                       -Subnet_VDMS_Name $WPFSACA_VDMS_Name.Text `
                                       -Subnet_VDMS $WPFSACA_VDMS_Subnet.Text `
                                       -Verbose
    }
    else
    {
    Write-Host "Skipping 1 Tier SACA Networking Build" -ForegroundColor Yellow
    }
    #####################################################################################################    
    # SACA F5 Tier 3 Build
        if ($WPFSACA.IsChecked -eq $true -and $WPFSACA_Tier.SelectionBoxItem -eq "SACA 3 Tier"){
    Write-Host "Building SACA F5 Tier 3 Build" -ForegroundColor Green

    # License BYOL
    Get-AzMarketplaceTerms -Publisher "f5-networks" -Product "f5-big-ip-byol" -Name "f5-big-all-2slot-byol" | Set-AzMarketplaceTerms -Accept -Verbose
    # License PAYG
    Get-AzMarketplaceTerms -Publisher "f5-networks" -Product "f5-big-ip-best" -Name "f5-bigip-virtual-edition-1g-best-hourly" | Set-AzMarketplaceTerms -Accept -Verbose

    New-AzResourceGroupDeployment      -TemplateFile .\Templates\SACA\3T_SACA_F5_Deploy.json `
                                       -ResourceGroupName $rg `
                                       -Name "F5_Build" `
                                       -StorageAccountName $WPFsaname1.Text `
                                       -adminUsername $WPFadminaccount1.Text `
                                       -adminPassword $WPFadminpassword1.SecurePassword `
                                       -BigIP_VM1_Name $WPFSACA_VM_BigIP1.Text `
                                       -BigIP_VM2_Name $WPFSACA_VM_BigIP2.Text `
                                       -BigIP_VM3_Name $WPFSACA_VM_BigIP3.Text `
                                       -BigIP_VM4_Name $WPFSACA_VM_BigIP4.Text `
                                       -IPS_FW0_Name $WPFSACA_VM_IPS1.Text `
                                       -IPS_FW1_Name $WPFSACA_VM_IPS2.Text `
                                       -SB_LB_Name $WPFSACA_SBLB.Text `
                                       -NB_LB_Name $WPFSACA_NBLB.Text `
                                       -SB_LB_IP $WPFSACA_SBLB_IP.Text `
                                       -BigIP_VM1_Size $WPFsacaBIGIP1vmsize.SelectedItem `
                                       -BigIP_VM2_Size $WPFsacaBIGIP2vmsize.SelectedItem `
                                       -BigIP_VM3_Size $WPFsacaBIGIP3vmsize.SelectedItem `
                                       -BigIP_VM4_Size $WPFsacaBIGIP4vmsize.SelectedItem `
                                       -BIGIP_VM1_ExternalPri_IP $WPFSACA_BIGIP1Ext1Pri_IP.Text `
                                       -BIGIP_VM1_ExternalSec_IP $WPFSACA_BIGIP1Ext1Sec_IP.Text `
                                       -BIGIP_VM1_InternalNPri_IP $WPFSACA_BIGIP1INTNPri_IP.Text `
                                       -BIGIP_VM1_InternalNSec_IP $WPFSACA_BIGIP1INTNSec_IP.Text `
                                       -BIGIP_VM2_ExternalPri_IP $WPFSACA_BIGIP2Ext1Pri_IP.Text `
                                       -BIGIP_VM2_ExternalSec_IP $WPFSACA_BIGIP2Ext1Sec_IP.Text `
                                       -BIGIP_VM2_InternalNPri_IP $WPFSACA_BIGIP2INTNPri_IP.Text `
                                       -BIGIP_VM2_InternalNSec_IP $WPFSACA_BIGIP2INTNSec_IP.Text `
                                       -BIGIP_VM3_External2Pri_IP $WPFSACA_BIGIP3Ext2Pri_IP.Text `
                                       -BIGIP_VM3_External2Sec_IP $WPFSACA_BIGIP3Ext2Sec_IP.Text `
                                       -BIGIP_VM3_InternalSPri_IP $WPFSACA_BIGIP3INTSPri_IP.Text `
                                       -BIGIP_VM3_InternalSSec_IP $WPFSACA_BIGIP3INTSSec_IP.Text `
                                       -BIGIP_VM4_External2Pri_IP $WPFSACA_BIGIP4Ext2Pri_IP.Text `
                                       -BIGIP_VM4_External2Sec_IP $WPFSACA_BIGIP4Ext2Sec_IP.Text `
                                       -BIGIP_VM4_InternalSPri_IP $WPFSACA_BIGIP4INTSPri_IP.Text `
                                       -BIGIP_VM4_InternalSSec_IP $WPFSACA_BIGIP4INTSSec_IP.Text `
                                       -BIGIP_VM1_Management_IP $WPFSACA_BIGIP1MGT_IP.Text `
                                       -BIGIP_VM2_Management_IP $WPFSACA_BIGIP2MGT_IP.Text `
                                       -BIGIP_VM3_Management_IP $WPFSACA_BIGIP3MGT_IP.Text `
                                       -BIGIP_VM4_Management_IP $WPFSACA_BIGIP4MGT_IP.Text `
                                       -VNetName $WPFSACA_VNET_Name.Text `
                                       -DNSLabel $WPFSACA_DNS.Text `
                                       -Location $WPFLocations1.SelectedItem `
                                       -Subnet_Management_Name $WPFSACA_MGT_Name.Text `
                                       -Subnet_External_Name $WPFSACA_EXT_Name.Text `
                                       -Subnet_External2_Name $WPFSACA_EXT2_Name.Text `
                                       -Subnet_InternalN_Name $WPFSACA_INTN_Name.Text `
                                       -Subnet_InternalS_Name $WPFSACA_INTS_Name.Text `
                                       -Subnet_IPSInt_Name $WPFSACA_IPSInt_Name.Text `
                                       -Subnet_IPSExt_Name $WPFSACA_IPSExt_Name.Text `
                                       -Subnet_VDMS_Name $WPFSACA_VDMS_Name.Text `
                                       -AsJob `
                                       -Verbose
    Start-Sleep -Seconds 5
    $SACA3Job = Get-Job | Select -Last 1
    If($SACA3Job.State -eq "Failed"){
    Write-Host "SACA 3 Tier Failed" -ForegroundColor Red
    Write-Host $SACA3Job.Error -ForegroundColor Red
    }
    Write-Host "SACA 3 Tier Job Is" $SACA3Job.State -ForegroundColor Green
    }
    else
    {
    Write-Host "Skipping SACA 3 Tier Build" -ForegroundColor Yellow
    }
    #####################################################################################################    
    # SACA F5 Tier 1 Build
        if ($WPFSACA.IsChecked -eq $true -and $WPFSACA_Tier.SelectionBoxItem -eq "SACA 1 Tier"){
    Write-Host "Building SACA F5 Tier 1 Build" -ForegroundColor Green

    # License BYOL
    Get-AzMarketplaceTerms -Publisher "f5-networks" -Product "f5-big-ip-byol" -Name "f5-big-all-2slot-byol" | Set-AzMarketplaceTerms -Accept -Verbose
    # License PAYG
    Get-AzMarketplaceTerms -Publisher "f5-networks" -Product "f5-big-ip-best" -Name "f5-bigip-virtual-edition-1g-best-hourly" | Set-AzMarketplaceTerms -Accept -Verbose

    New-AzResourceGroupDeployment      -TemplateFile .\Templates\SACA\1T_SACA_F5_Deploy.json `
                                       -ResourceGroupName $rg `
                                       -Name "F5_Build" `
                                       -StorageAccountName $WPFsaname1.Text `
                                       -adminUsername $WPFadminaccount1.Text `
                                       -adminPassword $WPFadminpassword1.SecurePassword `
                                       -BigIP_VM1_Name $WPFSACA_VM_BigIP1.Text `
                                       -BigIP_VM2_Name $WPFSACA_VM_BigIP2.Text `
                                       -SB_LB_Name $WPFSACA_SBLB.Text `
                                       -NB_LB_Name $WPFSACA_NBLB.Text `
                                       -SB_LB_IP $WPFSACA_SBLB_IP.Text `
                                       -BigIP_VM1_Size $WPFsacaBIGIP1vmsize.SelectedItem `
                                       -BigIP_VM2_Size $WPFsacaBIGIP2vmsize.SelectedItem `
                                       -BIGIP_VM1_ExternalPri_IP $WPFSACA_BIGIP1Ext1Pri_IP.Text `
                                       -BIGIP_VM1_ExternalSec_IP $WPFSACA_BIGIP1Ext1Sec_IP.Text `
                                       -BIGIP_VM1_InternalPri_IP $WPFSACA_BIGIP1INTPri_IP.Text `
                                       -BIGIP_VM1_InternalSec_IP $WPFSACA_BIGIP1INTSec_IP.Text `
                                       -BIGIP_VM2_ExternalPri_IP $WPFSACA_BIGIP2Ext1Pri_IP.Text `
                                       -BIGIP_VM2_ExternalSec_IP $WPFSACA_BIGIP2Ext1Sec_IP.Text `
                                       -BIGIP_VM2_InternalPri_IP $WPFSACA_BIGIP2INTPri_IP.Text `
                                       -BIGIP_VM2_InternalSec_IP $WPFSACA_BIGIP2INTSec_IP.Text `
                                       -BIGIP_VM1_Management_IP $WPFSACA_BIGIP1MGT_IP.Text `
                                       -BIGIP_VM2_Management_IP $WPFSACA_BIGIP2MGT_IP.Text `
                                       -VNetName $WPFSACA_VNET_Name.Text `
                                       -DNSLabel $WPFSACA_DNS.Text `
                                       -Location $WPFLocations1.SelectedItem `
                                       -Subnet_Management_Name $WPFSACA_MGT_Name.Text `
                                       -Subnet_External_Name $WPFSACA_EXT_Name.Text `
                                       -Subnet_InternalS_Name $WPFSACA_INTS_Name.Text `
                                       -Subnet_VDMS_Name $WPFSACA_VDMS_Name.Text `
                                       -AsJob `
                                       -Verbose
    Start-Sleep -Seconds 5
    $SACA1Job = Get-Job | Select -Last 1
    If($SACA1Job.State -eq "Failed"){
    Write-Host "SACA 1 Tier Failed" -ForegroundColor Red
    Write-Host $SACA1Job.Error -ForegroundColor Red
    }
    Write-Host "SACA 1 Tier Job Is" $SACA1Job.State -ForegroundColor Green
    }
    else
    {
    Write-Host "Skipping SACA 1 Tier Build" -ForegroundColor Yellow
    }
    ##################################################################################################### 
    # SACA IPS Build
        if ($WPFSACA.IsChecked -eq $true -and $WPFSACA_Tier.SelectionBoxItem -eq "SACA 3 Tier"){
    Write-Host "Building SACA IPS/FW Build" -ForegroundColor Green

    New-AzResourceGroupDeployment      -TemplateFile .\Templates\SACA\3T_SACA_IPSDeploy.json `
                                       -ResourceGroupName $rg `
                                       -Name "IPSFW_Build" `
                                       -StorageAccountName $WPFsaname1.Text `
                                       -adminUsername $WPFadminaccount1.Text `
                                       -adminPassword $WPFadminpassword1.SecurePassword `
                                       -VNetName $WPFSACA_VNET_Name.Text `
                                       -DNSLabel $WPFSACA_DNS.Text `
                                       -Location $WPFLocations1.SelectedItem `
                                       -Subnet_Management_Name $WPFSACA_MGT_Name.Text `
                                       -Subnet_External_Name $WPFSACA_EXT_Name.Text `
                                       -Subnet_External2_Name $WPFSACA_EXT2_Name.Text `
                                       -Subnet_InternalN_Name $WPFSACA_INTN_Name.Text `
                                       -Subnet_InternalS_Name $WPFSACA_INTS_Name.Text `
                                       -Subnet_IPSInt_Name $WPFSACA_IPSInt_Name.Text `
                                       -Subnet_IPSExt_Name $WPFSACA_IPSExt_Name.Text `
                                       -Subnet_VDMS_Name $WPFSACA_VDMS_Name.Text `
                                       -IPS1ExtPri_IP $WPFSACA_IPS1ExternalPri_IP.Text `
                                       -IPS1ExtSec_IP $WPFSACA_IPS1ExternalSec_IP.Text `
                                       -IPS2ExtPri_IP $WPFSACA_IPS2ExternalPri_IP.Text `
                                       -IPS2ExtSec_IP $WPFSACA_IPS2ExternalSec_IP.Text `
                                       -IPSLB_IP $WPFSACA_IPSLB_IP.Text `
                                       -IPS1IntPri_IP $WPFSACA_IPS1InternalPri_IP.Text `
                                       -IPS1IntSec_IP $WPFSACA_IPS1InternalSec_IP.Text `
                                       -IPS2IntPri_IP $WPFSACA_IPS2InternalPri_IP.Text `
                                       -IPS2IntSec_IP $WPFSACA_IPS2InternalSec_IP.Text `
                                       -IPS1MGMT_IP $WPFSACA_IP1SMGT_IP.Text `
                                       -IPS2MGMT_IP $WPFSACA_IP2SMGT_IP.Text `
                                       -IPS_FW0_Size $WPFsacaFWIPS1vmsize.Text `
                                       -IPS_FW1_Size $WPFsacaFWIPS2vmsize.Text `
                                       -IPS_FW0_Name $WPFSACA_VM_IPS1.Text `
                                       -IPS_FW1_Name $WPFSACA_VM_IPS2.Text `
                                       -IPS_LB_Name $WPFSACA_IPSLB.Text `
                                       -AsJob `
                                       -Verbose
    Start-Sleep -Seconds 5
    $IPSJob = Get-Job | Select -Last 1
    If($IPSJob.State -eq "Failed"){
    Write-Host "SACA IPS Failed" -ForegroundColor Red
    Write-Host $IPSJob.Error -ForegroundColor Red
        }
    Write-Host "SACA IPS Job Is" $IPSJob.State -ForegroundColor Green
    }
    else
    {
    Write-Host "Skipping SACA 3 Tier IPS Build" -ForegroundColor Yellow
    }

    #####################################################################################################    
    # Bastion Build
    if ($WPFBastion.IsChecked -eq $true){
    Write-Host "Building Bastion Host" -ForegroundColor Green

    $bastionnet = Get-AzVirtualNetwork -Name $VirtualNetworkName -ResourceGroupName $rg
    $bastionnet.AddressSpace.AddressPrefixes.Add($bastionsubnet)
    Set-AzVirtualNetwork -VirtualNetwork $bastionnet    
    #New-AzVirtualNetworkSubnetConfig -Name "AzureBastionSubnet" -AddressPrefix $bastionsubnet
    #$bastionIP = New-AzPublicIpAddress -Name "AzureBastionSubnet-PIP" -ResourceGroupName $rg -Location $AzureLocation -Sku Standard -AllocationMethod Static -IpAddressVersion IPv4
    #New-AzBastion -ResourceGroupName $rg -Name "Bastion" -PublicIpAddressId $bastionIP.id -VirtualNetworkId $vnet.Id
    
    New-AzResourceGroupDeployment @commonVariables `
                                       -Name "BastionHost" `
                                       -TemplateFile .\Templates\Bastion.json `
                                       -Verbose

    }
    else
    {
    Write-Host "Skipping Bastion Host Build" -ForegroundColor Yellow
    }

    #####################################################################################################    
    # Get Subnet info and Disable Private EndPoint network policies
    $vnet = Get-AzVirtualNetwork -ResourceGroupName $rg -Name $VirtualNetworkName -Verbose
    $subnet = $vnet | Select-Object -ExpandProperty subnets | Where-Object Name -eq $subnetname
    $subnet.PrivateEndpointNetworkPolicies = "Disabled"
    $vnet | Set-AzVirtualNetwork

    # Set DNS servers on VNET
    if ($WPFserver1.IsChecked -eq $true){
    $array = @($WPFserver1IP.Text, "168.63.129.16")
    $object = new-object -type PSObject -Property @{"DnsServers" = $array}
    $vnet.DhcpOptions = $object
    $vnet|Set-AzVirtualNetwork
    }

    if ($WPFserver1.IsChecked -eq $true -and $WPFDC_Count.SelectionBoxItem -eq "2"){
    $IP2 = $WPFserver1IP.Text.Split('.')
    $IP2[-1] = "40"
    $IP2 = $IP2 -join "."
    $array = @($WPFserver1IP.Text, $IP2, "168.63.129.16")
    $object = new-object -type PSObject -Property @{"DnsServers" = $array}
    $vnet.DhcpOptions = $object
    $vnet|Set-AzVirtualNetwork
    }

    # Setup Private EndPoints and DNS Zone
    $context = Get-AzContext
    If($context.Environment.Name -EQ "AzureUSGovernment"){
    $blobURL = "blob.core.usgovcloudapi.net"
    }
    else
    {
    $blobURL = "blob.core.windows.net"
    }

    $privateEndpointConnection = New-AzPrivateLinkServiceConnection -Name "PrivateConnection" -PrivateLinkServiceId $storageaccount.Id -GroupId 'blob' -Verbose
    New-AzPrivateEndpoint -Name "PrivateStorage" -ResourceGroupName $rg -Location $AzureLocation -Subnet $Subnet -PrivateLinkServiceConnection $privateEndpointConnection -Verbose
    $zone = New-AzPrivateDnsZone -ResourceGroupName $rg -Name $blobURL -Verbose
    $link = New-AzPrivateDnsVirtualNetworkLink -ResourceGroupName $rg -ZoneName $blobURL -Name "Storage-Link" -VirtualNetworkId $vnet.Id -Verbose
    $config = New-AzPrivateDnsZoneConfig -Name $blobURL -PrivateDnsZoneId $Zone.ResourceId -Verbose
    New-AzPrivateDnsZoneGroup -ResourceGroupName $rg -Name "ZoneGroup" -PrivateEndpointName "PrivateStorage" -PrivateDnsZoneConfig $Config -Verbose
    

    # Domain Private DNS
    New-AzPrivateDnsZone -ResourceGroupName $rg -Name $DomainName -Verbose
    New-AzPrivateDnsVirtualNetworkLink -ResourceGroupName $rg -ZoneName $DomainName -Name "Domain-Link" -VirtualNetworkId $vnet.Id -Verbose -EnableRegistration

    #####################################################################################################    
    # DC/CA Build
    if ($WPFserver1.IsChecked -eq $true){
    Write-Host "Building DC/CA" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFServer1Name.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFserver1vmsize.SelectedItem `
                                       -vmdisk $WPFserver1disk.SelectedItem `
                                       -publisher "MicrosoftWindowsServer" `
                                       -offer "WindowsServer" `
                                       -sku $WPFserver1image.SelectedItem `
                                       -servername $WPFServer1Name.Text `
                                       -ip $WPFserver1IP.Text `
                                       -role $WPFServer1Role.Text `
                                       -AsJob `
                                       -Verbose
    Start-Sleep -Seconds 5
    $DCJob = Get-Job | Select -Last 1
        If($DCJob.State -eq "Failed"){
        Write-Host "DC Failed" -ForegroundColor Red
        Write-Host $DCJob.Error -ForegroundColor Red
        }
        Write-Host "DC Job Is" $DCJob.State -ForegroundColor Green
    }
    else
    {
    Write-Host "Skipping DC Build" -ForegroundColor Yellow
    }
    #####################################################################################################    
    # Add Another DC Build
    if ($WPFDC_Count.SelectionBoxItem -eq "2"){
    Write-Host "Adding Second DC" -ForegroundColor Green
    New-AzResourceGroupDeployment @commonVariables `
                                       -Name "DC02" `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFserver1vmsize.SelectedItem `
                                       -vmdisk $WPFserver1disk.SelectedItem `
                                       -publisher "MicrosoftWindowsServer" `
                                       -offer "WindowsServer" `
                                       -sku $WPFserver1image.SelectedItem `
                                       -servername "DC02" `
                                       -ip $IP2 `
                                       -role "AddDC" `
                                       -AsJob `
                                       -Verbose
    Start-Sleep -Seconds 5
    #$DCJob = Get-Job | Where Name -Like *DC* | Select -Last 1
    $DC2Job = Get-Job | Select -Last 1
        If($DC2Job.State -eq "Failed"){
        Write-Host "DC2 Failed" -ForegroundColor Red
        Write-Host $DC2Job.Error -ForegroundColor Red
        }
        Write-Host "DC2 Job Is" $ADFSJob.State -ForegroundColor Green
    }
    else
    {
    Write-Host "Skipping DC Build" -ForegroundColor Yellow
    }
    #####################################################################################################
    # ADFS Build
    if ($WPFADFS.IsChecked -eq $true){
    Write-Host "Building ADFS" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFADFSName.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFadfssize.SelectedItem `
                                       -vmdisk $WPFadfsdisk.SelectedItem `
                                       -publisher "MicrosoftWindowsServer" `
                                       -offer "WindowsServer" `
                                       -sku $WPFADFSimage.SelectedItem `
                                       -servername $WPFADFSName.Text `
                                       -ip $WPFADFSIP.Text `
                                       -role $WPFADFSRole.Text `
                                       -AsJob `
                                       -Verbose
    Start-Sleep -Seconds 5
        $ADFSJob = Get-Job | Select -Last 1
        If($ADFSJob.State -eq "Failed"){
        Write-Host "ADFS Failed" -ForegroundColor Red
        Write-Host $ADFSJob.Error -ForegroundColor Red
        }
        Write-Host "ADFS Job Is" $ADFSJob.State -ForegroundColor Green
    }
    else
    {
    Write-Host "Skipping ADFS Build" -ForegroundColor Yellow
    }
    #####################################################################################################
    # Exchange Build
    if ($WPFExchange.IsChecked -eq $true){
    Write-Host "Building Exchange" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFExName.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFexsize.SelectedItem `
                                       -vmdisk $WPFexdisk.SelectedItem `
                                       -publisher "MicrosoftWindowsServer" `
                                       -offer "WindowsServer" `
                                       -sku $WPFeximage.SelectedItem `
                                       -servername $WPFexName.Text `
                                       -ip $WPFexIP.Text `
                                       -role $WPFexRole.Text `
                                       -AsJob `
                                       -Verbose
        Start-Sleep -Seconds 5
        $EXJob = Get-Job | Select -Last 1
        If($EXJob.State -eq "Failed"){
        Write-Host "Exchange Failed" -ForegroundColor Red
        Write-Host $EXJob.Error -ForegroundColor Red
        }
        Write-Host "Exchange Job Is" $EXJob.State -ForegroundColor Green
    }
    else
    {
    Write-Host "Skipping Exchange Build" -ForegroundColor Yellow
    }
    #####################################################################################################                                   
    # SCCM Build
    if ($WPFSCCM.IsChecked -eq $true){
    Write-Host "Building SCCM Primary Server, SCCM Primary will take up to 45mins to install once the DSC starts" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFsccm_ps_name.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFsscm_ps_size.SelectedItem `
                                       -vmdisk $WPFsccm_ps_disk.SelectedItem `
                                       -publisher "MicrosoftSQLServer" `
                                       -offer $WPFsccmimageoffer.SelectedItem `
                                       -sku $WPFsccmimagesku.SelectedItem `
                                       -servername $WPFsccm_ps_name.Text `
                                       -ip $WPFsccm_ps_ip.Text `
                                       -role "PS" `
                                       -AsJob `
                                       -Verbose
        Start-Sleep -Seconds 5
        $PSJob = Get-Job | Select -Last 1
        If($PSJob.State -eq "Failed"){
        Write-Host "SCCM Primary Failed" -ForegroundColor Red
        Write-Host $PSJob.Error -ForegroundColor Red
        }
        Write-Host "SCCM Primary Job Is" $PSJob.State -ForegroundColor Green


    Write-Host "Building SCCM DP/MP Server" -ForegroundColor Green
    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFsccm_dp_name.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFsccm_mpdp_size.SelectedItem `
                                       -vmdisk $WPFsccm_mpdp_disk.SelectedItem `
                                       -publisher "MicrosoftWindowsServer" `
                                       -offer "WindowsServer" `
                                       -sku $WPFsccmdpimage.SelectedItem `
                                       -servername $WPFsccm_dp_name.Text `
                                       -ip $WPFsccm_dp_ip.Text `
                                       -role "DPMP" `
                                       -AsJob `
                                       -Verbose
        Start-Sleep -Seconds 5
        $DPJob = Get-Job | Select -Last 1
        If($DPJob.State -eq "Failed"){
        Write-Host "SCCM DP/MP Failed" -ForegroundColor Red
        Write-Host $DPJob.Error -ForegroundColor Red
        }
        Write-Host "SCCM DP/MP Job Is" $DPJob.State -ForegroundColor Green
    }
    else
    {
    Write-Host "Skipping SCCM Build" -ForegroundColor Yellow                                                                                                            
    }
    #####################################################################################################
    # Workstation Build
    if ($WPFworkstation.IsChecked -eq $true){
    Write-Host "Building Windows Workstation" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFworkstationName.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFworkstationsize.SelectedItem `
                                       -vmdisk $WPFworkstationdisk.SelectedItem `
                                       -publisher "MicrosoftWindowsDesktop" `
                                       -offer "windows-10" `
                                       -sku $WPFworkstationimage.SelectedItem `
                                       -servername $WPFworkstationName.Text `
                                       -ip $WPFworkstationIP.Text `
                                       -role $WPFworkstationRole.Text `
                                       -AsJob `
                                       -Verbose
        Start-Sleep -Seconds 5
        $WKJob = Get-Job | Select -Last 1
        If($WKJob.State -eq "Failed"){
        Write-Host "Workstation Failed" -ForegroundColor Red
        Write-Host $WKJob.Error -ForegroundColor Red
        }
        Write-Host "Workstation Job Is" $WKJob.State -ForegroundColor Green

    }
    else
    {
    Write-Host "Skipping Windows Client Build" -ForegroundColor Yellow                                                                                                          
    }     
    #####################################################################################################
    # SharePoint Build
    if ($WPFsharepoint.IsChecked -eq $true){
    Write-Host "Building SQL and SharePoint" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFSQLName.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFsharepoint_size.SelectedItem `
                                       -vmdisk $WPFSQLDisk.SelectedItem `
                                       -publisher "MicrosoftSQLServer" `
                                       -offer $WPFSQLImage.SelectedItem `
                                       -sku $WPFSQLSKU.SelectedItem `
                                       -servername $WPFSQLName.Text `
                                       -ip $WPFSQLIP.Text `
                                       -role $WPFSQLRole.Text `
                                       -AsJob `
                                       -Verbose
        Start-Sleep -Seconds 5
        $SQLJob = Get-Job | Select -Last 1
        If($SQLJob.State -eq "Failed"){
        Write-Host "SQL Failed" -ForegroundColor Red
        Write-Host $SQLJob.Error -ForegroundColor Red
        }
        Write-Host "SQL Job Is" $SQLJob.State -ForegroundColor Green


    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFsharepointName.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFsharepoint_size.SelectedItem `
                                       -vmdisk $WPFsharepoint_disk.SelectedItem `
                                       -publisher "MicrosoftSharePoint" `
                                       -offer "MicrosoftSharePointServer" `
                                       -sku $WPFsharepointimage.SelectedItem `
                                       -servername $WPFsharepointName.Text `
                                       -ip $WPFsharepointIP.Text `
                                       -role $WPFsharepointRole.Text `
                                       -AsJob `
                                       -Verbose
        Start-Sleep -Seconds 5
        $SPJob = Get-Job | Select -Last 1
        If($SPJob.State -eq "Failed"){
        Write-Host "SharePoint Failed" -ForegroundColor Red
        Write-Host $SPJob.Error -ForegroundColor Red
        }
        Write-Host "SharePoint Job Is" $SPJob.State -ForegroundColor Green


    }
    else
    {
    Write-Host "Skipping SharePoint Build" -ForegroundColor Yellow
    }
    #####################################################################################################
    # Extra Server Build
    if ($WPFserver5.IsChecked -eq $true){
    Write-Host "Building" $WPFServer5Name.Text -ForegroundColor Green
    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFServer5Name.Text `
                                       -TemplateFile $VMTemplate `
                                       -vmsize $WPFserver5size.SelectedItem `
                                       -vmdisk $WPFserver5disk.SelectedItem `
                                       -publisher "MicrosoftWindowsServer" `
                                       -offer "WindowsServer" `
                                       -sku $WPFserver5image.SelectedItem `
                                       -servername $WPFServer5Name.Text `
                                       -ip $WPFserver5IP.Text `
                                       -role $WPFServer5Role.Text `
                                       -AsJob `
                                       -Verbose
        Start-Sleep -Seconds 5
        $SRVJob = Get-Job | Select -Last 1
        If($SRVJob.State -eq "Failed"){
        Write-Host $WPFServer5Name.Text "Failed" -ForegroundColor Red
        Write-Host $SRVJob.Error -ForegroundColor Red
        }
        Write-Host $WPFServer5Name.Text "Job Is" $SRVJob.State -ForegroundColor Green
                                                                                                       
    }
    else
    {
    Write-Host "Skipping" $WPFServer5Name.Text "Build" -ForegroundColor Yellow
    }
    #####################################################################################################
    # WVD Build
    if ($WPFWVD.IsChecked -eq $true){
    Write-Host "Building Windows Virtual Desktop, will sleep for 11mins allow time for the DC to build." -ForegroundColor Green
    Start-Sleep -Seconds 660 -Verbose
    New-AzResourceGroupDeployment -TemplateFile .\Templates\AzureWVD.json -Name "WVD" `
                                  -hostpoolName $WPFWVD_HostName.text `
                                  -domain $DomainName `
                                  -ResourceGroupName $rg `
                                  -vmNamePrefix $Prefix `
                                  -hostpoolType "Pooled" `
                                  -vmSize $WPFWVD_Size.SelectedItem `
                                  -vmLocation $WPFLocations1.SelectedItem `
                                  -administratorAccountUsername $UserFQDN `
                                  -administratorAccountPassword $AdminPassword `
                                  -vmResourceGroup $rg `
                                  -vmNumberOfInstances $WPFWVD_NumberVMs.Text `
                                  -vmGalleryImageOffer "Office-365"`
                                  -vmGalleryImagePublisher "MicrosoftWindowsDesktop" `
                                  -vmGalleryImageSKU $WPFWVD_Image.SelectedItem `
                                  -vmDiskType $WPFWVD_Disk.SelectedItem `
                                  -vmImageType "Gallery" `
                                  -loadBalancerType "BreadthFirst" `
                                  -existingSubnetName $subnetname `
                                  -existingVnetName $VirtualNetworkName `
                                  -virtualNetworkResourceGroupName $rg `
                                  -location $WPFWVD_Metadata.SelectedItem `
                                  -addToWorkspace $false `
                                  -tokenExpirationTime $TokenExpireDate `
                                  -createAvailabilitySet $true `
                                  -AsJob `
                                  -Verbose

    }
    else
    {
    Write-Host "Skipping Windows Virtual Desktop Build" -ForegroundColor Yellow
    }

    #####################################################################################################


write-host "IaaS_Builder is finished, Login to Azure Portal and check Deployments under the Resource group" -ForegroundColor Green
})


#===========================================================================
# Shows the form
#===========================================================================
# write-host "To show the form, run the following" -ForegroundColor Cyan


$async = $Form.Dispatcher.InvokeAsync({
    $Form.ShowDialog() | out-null
})
$async.Wait() | Out-Null

Pop-Location