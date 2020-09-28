$DefaultVMSize = "Standard_F2s"
$DefaultVMDisk = "Premium_LRS"
$DefaultOSImage = "2019-Datacenter"
$DefaultOSWSImage = "19h2-ent"
$DefaultWVDImage = "20h1-evd-o365pp"

$AzureModule = Get-Module -ListAvailable -Name Az.*
    if ($AzureModule.Name -notlike "Az.*"){
    Write-Host "Can't find Azure Module, installing module"
    Install-Module Az -Force -Verbose -Scope CurrentUser
    Import-Module Az
    }
    else
    {
    Write-Host "Found Azure Module"
    #Import-Module Az
}

############  LOGIN SECTION  #############
Clear-Host

if (Get-AzContext) {
    Write-Host "We have connection, start building!!" -ForegroundColor Green
    Get-Azcontext |fl
    $Title = "Task Menu"
$Caption = @"

1 - Continue with current login
2 - Reconnect with different account Commerical Azure Account
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
 
$b = [System.Management.Automation.Host.ChoiceDescription]::new("&2 Reconnect Commerical")
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
1 - Connect to Commerical Azure Account
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

#Push-Location (Split-Path $MyInvocation.MyCommand.Path)
split-path $SCRIPT:MyInvocation.MyCommand.Path -parent
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
write-host "Found the following interactable elements from our form" -ForegroundColor Cyan
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

# Build Location List
    $WPFSubscription1.Add_DropDownClosed({
    $Locations = Get-AzLocation
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

<#if ((Get-AzContext).Environment.Name -eq 'AzureUSGovernment') {
    $WPFWVD_Metadata.Addchild("usgovvirginia")
    $WPFWVD_Metadata.Addchild("usgovarizona")
    }
    else
    {
        foreach($Location in $Locations){
        $WPFWVD_Metadata.AddChild($Location.DisplayName)
      }
    } #>

# Query VMSize and IMages After Location is Selected

$WPFLocations1.Add_SelectionChanged({

    #$vmsize = Get-AzVMSize -Location $WPFLocations1.SelectedItem | Where Name -Like "*f2s*"
    $vmsize = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Restrictions.ReasonCode -ne 'NotAvailableForSubscription' -and $_.ResourceType.Contains("virtualMachines")}
    
    $SQLoffers = Get-AzVMImageOffer -Location $WPFLocations1.SelectedItem -PublisherName "MicrosoftSQLServer" | Select offer
    $serverskus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -Offer "WindowsServer" -PublisherName "MicrosoftWindowsServer" | Select Skus    
    $clientskus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -Offer "Windows-10" -PublisherName "MicrosoftWindowsDesktop" | Select Skus
    $client365skus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -Offer "Office-365" -PublisherName "MicrosoftWindowsDesktop" | Select Skus
    $sharePointSkus = Get-AzVMImageSku -Location $WPFLocations1.SelectedItem -PublisherName MicrosoftSharePoint -Offer MicrosoftSharePointServer

    $WPFserver1disk.AddChild($DefaultVMDisk)
    $WPFadfsdisk.AddChild($DefaultVMDisk)
    $WPFexdisk.AddChild($DefaultVMDisk)
    $WPFsccm_ps_disk.AddChild($DefaultVMDisk)
    $WPFsccm_mpdp_disk.AddChild($DefaultVMDisk)
    $WPFsharepoint_disk.AddChild($DefaultVMDisk)
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
    $WPFSQLsize.SelectedItem = $DefaultVMDisk
    $WPFserver5size.SelectedItem = $DefaultVMSize
    $WPFserver5disk.SelectedItem = $DefaultVMDisk    
    $WPFworkstationsize.SelectedItem = $DefaultVMSize
    $WPFworkstationdisk.SelectedItem = $DefaultVMDisk 
    $WPFWVD_Size.SelectedItem = $DefaultVMSize
    $WPFWVD_Disk.SelectedItem = $DefaultVMDisk 

    
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
})
#End Load Images and Select Default

# Query StorageAccount Name
    $WPFsaname1.Add_LostFocus({
    $CheckSA = Get-AzStorageAccountNameAvailability -Name $WPFsaname1.Text
    If($CheckSA.NameAvailable -eq $false){
    Write-host "SA Name Not Available"
    $WPFSA.Foreground = "#FFF21802" #Red
    $WPFSA.Text = "Not Available"
    $WPFsaname1.BorderBrush = '#FFF21802'
    }
    else
    {
    Write-Host "SA Name Available"
    $WPFSA.Foreground = "#FF068113" #Green
    $WPFSA.Text = "Available"
    $WPFsaname1.BorderBrush = '#FF068113'
    }
    })
    
    $WPFsaname1.Add_Loaded({
    $CheckSA = Get-AzStorageAccountNameAvailability -Name $WPFsaname1.Text
    If($CheckSA.NameAvailable -eq $false){
    Write-host "SA Name Not Available"
    $WPFSA.Foreground = "#FFF21802" #Red
    $WPFSA.Text = "Not Available"
    $WPFsaname1.BorderBrush = '#FFF21802'
    }
    else
    {
    Write-Host "SA Name Available"
    $WPFSA.Foreground = "#FF068113" #Green
    $WPFSA.Text = "Available"
    $WPFsaname1.BorderBrush = '#FF068113'
    }
    })
     
# End StorageAccount Name

#Query Disk type Premium_LRS or Standard_LRS
    $WPFserver1vmsize.Add_SelectionChanged({
    $WPFserver1disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFserver1vmsize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFserver1disk.AddChild("Premium_LRS")
        $WPFserver1disk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFserver1disk.AddChild("Standard_LRS")
        $WPFserver1disk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFadfssize.Add_SelectionChanged({
    $WPFadfsdisk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFadfssize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFadfsdisk.AddChild("Premium_LRS")
        $WPFadfsdisk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFadfsdisk.AddChild("Standard_LRS")
        $WPFadfsdisk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFexsize.Add_SelectionChanged({
    $WPFexdisk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFexsize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFexdisk.AddChild("Premium_LRS")
        $WPFexdisk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFexdisk.AddChild("Standard_LRS")
        $WPFexdisk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFsscm_ps_size.Add_SelectionChanged({
    $WPFsccm_ps_disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFsscm_ps_size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFsccm_ps_disk.AddChild("Premium_LRS")
        $WPFsccm_ps_disk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFsccm_ps_disk.AddChild("Standard_LRS")
        $WPFsccm_ps_disk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFsccm_mpdp_size.Add_SelectionChanged({
    $WPFsccm_mpdp_disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFsccm_mpdp_size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFsccm_mpdp_disk.AddChild("Premium_LRS")
        $WPFsccm_mpdp_disk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFsccm_mpdp_disk.AddChild("Standard_LRS")
        $WPFsccm_mpdp_disk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFsharepoint_size.Add_SelectionChanged({
    $WPFsharepoint_disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFsharepoint_size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFsharepoint_disk.AddChild("Premium_LRS")
        $WPFsharepoint_disk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFsharepoint_disk.AddChild("Standard_LRS")
        $WPFsharepoint_disk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFSQLSize.Add_SelectionChanged({
    $WPFSQLDisk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFSQLSize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFSQLDisk.AddChild("Premium_LRS")
        $WPFSQLDisk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFSQLDisk.AddChild("Standard_LRS")
        $WPFSQLDisk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFserver5size.Add_SelectionChanged({
    $WPFserver5disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFserver5size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFserver5disk.AddChild("Premium_LRS")
        $WPFserver5disk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFserver5disk.AddChild("Standard_LRS")
        $WPFserver5disk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFworkstationsize.Add_SelectionChanged({
    $WPFworkstationdisk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFworkstationsize.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities

        if($diskinfo[7].Value -eq $True){
        $WPFworkstationdisk.AddChild("Premium_LRS")
        $WPFworkstationdisk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFworkstationdisk.AddChild("Standard_LRS")
        $WPFworkstationdisk.SelectedItem = "Standard_LRS"
        }
    })

    $WPFWVD_Size.Add_SelectionChanged({
    $WPFWVD_Disk.Items.Clear()
    $diskinfo = Get-AzComputeResourceSku | Where-Object {$_.Locations -eq ($WPFLocations1.SelectedItem) -and $_.Name.Contains($WPFWVD_Size.SelectedItem) -and $_.ResourceType.Contains("virtualMachines")} | Select -ExpandProperty Capabilities
        if($diskinfo[7].Value -eq $True){
        $WPFWVD_Disk.AddChild("Premium_LRS")
        $WPFWVD_Disk.SelectedItem = "Premium_LRS"
        }
        else
        {
        $WPFWVD_Disk.AddChild("Standard_LRS")
        $WPFWVD_Disk.SelectedItem = "Standard_LRS"
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
    $vnet = $prefix + '-vnet'
    $TokenExpireDate = $((get-date).ToUniversalTime().AddDays(1).ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ'))
    $UserFQDN = $WPFadminaccount1.Text + "@" + $WPFDname1.Text

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
            Set-AzStorageBlobContent -Container $dsccontainer.Name -File .\DSC\ServerDomainJoin.zip -Blob 'ServerDomainJoin.ps1.zip' -Context $dsccontainer.Context -Force -Verbose -AsJob
            Get-AzStorageContainer -Name $dsccontainer.Name -Context $dsccontainer.Context -Verbose
        
            write-host "Sleeping for 30secs so the DSC files can upload" -ForegroundColor Green
            Start-Sleep -Seconds 30
            $DSCs = Get-AzStorageBlob -Container dsc -Context $dsccontainer.Context -Verbose
        
            # Get uri DSC for Deployment
            $assetLocation = (Get-AzStorageBlob -blob 'Configuration.zip' -Container 'dsc' -Context $dsccontainer.Context).context.BlobEndPoint #+ 'dsc/'
        
            # Setting up File Share Connections for each server
            #$Fileshare = $storageaccount.Context.FileEndPoint
            #$Fileshare = $Fileshare -replace 'https://','\\' -replace '/',''
            #$Fileshare = $fileshare + "\dscstatus"
            #$storageKey = Get-AzStorageAccountKey -ResourceGroupName $rg -Name $storageaccount.storageAccountName
            #$accountKey = ConvertTo-SecureString -String "$storageKey" -AsPlainText -Force
            #$sacreds = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList "Azure\$storageaccount.storageAccountName", $accountKey


    #####################################################################################################
    #Common variables
    $commonVariables = @{
    ResourceGroupName = $rg;
    TemplateFile = ".\AzureTemplate.json";
    prefix = $Prefix;
    DomainName = $DomainName;
    adminUsername = $adminAccount;
    adminPassword = $AdminPassword;
    _artifactsLocation = $assetLocation;
    addressprefix = $addressprefix;
    addresssubnet = $addressubnet;
    subnetname = $subnetname;
    DCName = $WPFServer1Name.Text;
    DCip = $WPFserver1IP.Text;
    DPMPName = $WPFsccm_dp_name.Text;
    PSName = $WPFsccm_ps_name.Text;
    STIG = $WPFSTIGs.IsChecked;
    sharePointVersion = $WPFsharepointimage.SelectedItem;
    SQLName = $WPFSQLName.Text
    BastionSubnet = $bastionsubnet
    }
    # DC/CA Build
    if ($WPFserver1.IsChecked -eq $true){
    Write-Host "Building DC/CA" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFServer1Name.Text `
                                       -vmsize $WPFserver1vmsize.SelectedItem `
                                       -vmdisk $WPFserver1disk.SelectedItem `
                                       -publisher "MicrosoftWindowsServer" `
                                       -offer "WindowsServer" `
                                       -sku $WPFserver1image.SelectedItem `
                                       -servername $WPFServer1Name.Text `
                                       -ip $WPFserver1IP.Text `
                                       -role $WPFServer1Role.Text `
                                       -Verbose
    #write-host "Sleeping for 90secs" -ForegroundColor Green
    #Start-Sleep -Seconds 90
    }
    else
    {
    Write-Host "Not checked, not true, dc 1 will always be check"
    }
    #####################################################################################################
    # ADFS Build
    if ($WPFADFS.IsChecked -eq $true){
    Write-Host "Building ADFS" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFADFSName.Text `
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
    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60
    }
    else
    {
    Write-Host "Will not build ADFS because someone forgot to check the box...."
    }
    #####################################################################################################
    # Exchange Build
    if ($WPFExchange.IsChecked -eq $true){
    Write-Host "Building Exchange" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFExName.Text `
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
    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60
    }
    else
    {
    Write-Host "Will not build Exchange because someone forgot to check the box...."
    }
    #####################################################################################################                                   
    # SCCM Build
    if ($WPFSCCM.IsChecked -eq $true){
    Write-Host "Building SCCM Primary Server, SCCM Primary will take up to 45mins to install once the DSC starts" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFsccm_ps_name.Text `
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

    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60

    Write-Host "Building SCCM DP/MP Server" -ForegroundColor Green
    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFsccm_dp_name.Text `
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

    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60
    }
    else
    {
    Write-Host "Will not build SCCM because someone forgot to check the box...."                                                                                                            
    }
    #####################################################################################################
    # Workstation Build
    if ($WPFworkstation.IsChecked -eq $true){
    Write-Host "Building Windows Workstation" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFworkstationName.Text `
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

    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60
    }
    else
    {
    Write-Host "Will not build Workstation because someone forgot to check the box...."                                                                                                            
    }     
    #####################################################################################################
    # SharePoint Build
    if ($WPFsharepoint.IsChecked -eq $true){
    Write-Host "Building SQL and SharePoint" -ForegroundColor Green

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFSQLName.Text `
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

    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60

    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFsharepointName.Text `
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

    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60

    }
    else
    {
    Write-Host "Will not build SharePoint because someone forgot to check the box...."
    }
    #####################################################################################################
    # Server Build
    if ($WPFserver5.IsChecked -eq $true){
    Write-Host "Here we go!!  Building" $WPFServer5Name.Text -ForegroundColor Green
    New-AzResourceGroupDeployment @commonVariables `
                                       -Name $WPFServer5Name.Text `
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
                                                                                                       
    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60

    }
    else
    {
    Write-Host "Will not build server because someone forgot to check the box...."
    }
    #####################################################################################################
    # WVD Build
    if ($WPFWVD.IsChecked -eq $true){
    Write-Host "Building Windows Virtual Desktop" -ForegroundColor Green
    
    New-AzResourceGroupDeployment -TemplateFile .\AzureWVD.json -Name "WVD" `
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
                                  -existingSubnetName $WPFsubnetName1.Text `
                                  -existingVnetName $vnet `
                                  -virtualNetworkResourceGroupName $rg `
                                  -location $WPFWVD_Metadata.SelectedItem `
                                  -addToWorkspace $false `
                                  -tokenExpirationTime $TokenExpireDate `
                                  -createAvailabilitySet $true


                                                                                                     
    write-host "Sleeping for 60secs" -ForegroundColor Green
    Start-Sleep -Seconds 60

    }
    else
    {
    Write-Host "Will not build WVD because someone forgot to check the box...."
    }
})


#===========================================================================
# Shows the form
#===========================================================================
write-host "To show the form, run the following" -ForegroundColor Cyan


$async = $Form.Dispatcher.InvokeAsync({
    $Form.ShowDialog() | out-null
})
$async.Wait() | Out-Null



Pop-Location