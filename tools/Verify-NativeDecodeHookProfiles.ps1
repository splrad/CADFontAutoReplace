param(
    [string]$ChorePath = (Join-Path (Resolve-Path ".").Path "chore")
)

$ErrorActionPreference = "Stop"

function Read-U16([byte[]]$Data, [int]$Offset) {
    [BitConverter]::ToUInt16($Data, $Offset)
}

function Read-U32([byte[]]$Data, [int]$Offset) {
    [BitConverter]::ToUInt32($Data, $Offset)
}

function Read-U64([byte[]]$Data, [int]$Offset) {
    [BitConverter]::ToUInt64($Data, $Offset)
}

function Read-AsciiZ([byte[]]$Data, [int]$Offset) {
    $end = $Offset
    while ($end -lt $Data.Length -and $Data[$end] -ne 0) {
        $end++
    }

    [Text.Encoding]::ASCII.GetString($Data, $Offset, $end - $Offset)
}

function Get-PeImage([string]$Path) {
    $data = [IO.File]::ReadAllBytes($Path)
    $pe = Read-U32 $data 0x3C
    $sectionCount = Read-U16 $data ($pe + 6)
    $optionalHeaderSize = Read-U16 $data ($pe + 20)
    $magic = Read-U16 $data ($pe + 24)
    $imageBase = if ($magic -eq 0x20B) {
        Read-U64 $data ($pe + 48)
    } else {
        [uint64](Read-U32 $data ($pe + 52))
    }
    $dataDirectoryOffset = $pe + 24 + $(if ($magic -eq 0x20B) { 112 } else { 96 })
    $exportRva = Read-U32 $data $dataDirectoryOffset
    $sectionOffset = $pe + 24 + $optionalHeaderSize
    $sections = @()
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $offset = $sectionOffset + ($i * 40)
        $sections += [pscustomobject]@{
            Name        = Read-AsciiZ $data $offset
            VirtualSize = Read-U32 $data ($offset + 8)
            VirtualRva  = Read-U32 $data ($offset + 12)
            RawSize     = Read-U32 $data ($offset + 16)
            RawOffset   = Read-U32 $data ($offset + 20)
        }
    }

    [pscustomobject]@{
        Path      = $Path
        Data      = $data
        ImageBase = $imageBase
        ExportRva = $exportRva
        Sections  = $sections
    }
}

function Convert-RvaToOffset($Image, [uint32]$Rva) {
    foreach ($section in $Image.Sections) {
        $size = [Math]::Max($section.VirtualSize, $section.RawSize)
        if ($Rva -ge $section.VirtualRva -and $Rva -lt ($section.VirtualRva + $size)) {
            return [int]($section.RawOffset + ($Rva - $section.VirtualRva))
        }
    }

    throw "RVA 0x$($Rva.ToString('X')) is outside PE sections: $($Image.Path)"
}

function Convert-OffsetToRva($Image, [int]$Offset) {
    foreach ($section in $Image.Sections) {
        if ($Offset -ge $section.RawOffset -and $Offset -lt ($section.RawOffset + $section.RawSize)) {
            return [uint32]($section.VirtualRva + ($Offset - $section.RawOffset))
        }
    }

    return $null
}

function Find-BytePattern([byte[]]$Data, [byte[]]$Pattern) {
    $hits = New-Object 'System.Collections.Generic.List[int]'
    if ($Pattern.Length -eq 0 -or $Data.Length -lt $Pattern.Length) {
        return $hits
    }

    for ($i = 0; $i -le ($Data.Length - $Pattern.Length); $i++) {
        if ($Data[$i] -ne $Pattern[0]) {
            continue
        }

        $matched = $true
        for ($j = 1; $j -lt $Pattern.Length; $j++) {
            if ($Data[$i + $j] -ne $Pattern[$j]) {
                $matched = $false
                break
            }
        }

        if ($matched) {
            $hits.Add($i)
        }
    }

    $hits
}

function Test-AcDbImpTextVTableSlot($Image, [uint32]$ExpectedRva) {
    $typeNameBytes = [Text.Encoding]::ASCII.GetBytes(".?AVAcDbImpText@@")
    $typeNameHits = Find-BytePattern $Image.Data $typeNameBytes
    foreach ($typeNameOffset in $typeNameHits) {
        $typeNameRva = Convert-OffsetToRva $Image $typeNameOffset
        if ($null -eq $typeNameRva -or $typeNameRva -lt 0x10) {
            continue
        }

        $typeDescriptorRva = [uint32]($typeNameRva - 0x10)
        $typeDescriptorBytes = [BitConverter]::GetBytes($typeDescriptorRva)
        $typeDescriptorRefs = Find-BytePattern $Image.Data $typeDescriptorBytes
        foreach ($typeDescriptorRef in $typeDescriptorRefs) {
            $completeObjectLocatorOffset = $typeDescriptorRef - 12
            if ($completeObjectLocatorOffset -lt 0 -or ($completeObjectLocatorOffset + 24) -gt $Image.Data.Length) {
                continue
            }

            $completeObjectLocatorRva = Convert-OffsetToRva $Image $completeObjectLocatorOffset
            if ($null -eq $completeObjectLocatorRva) {
                continue
            }

            $actualTypeDescriptorRva = Read-U32 $Image.Data ($completeObjectLocatorOffset + 12)
            if ($actualTypeDescriptorRva -ne $typeDescriptorRva) {
                continue
            }

            $selfRva = Read-U32 $Image.Data ($completeObjectLocatorOffset + 20)
            if ($selfRva -ne 0 -and $selfRva -ne $completeObjectLocatorRva) {
                continue
            }

            $locatorPointerBytes = [BitConverter]::GetBytes([uint64]($Image.ImageBase + $completeObjectLocatorRva))
            $locatorPointerRefs = Find-BytePattern $Image.Data $locatorPointerBytes
            foreach ($locatorPointerRef in $locatorPointerRefs) {
                $vtableRva = Convert-OffsetToRva $Image ($locatorPointerRef + 8)
                if ($null -eq $vtableRva) {
                    continue
                }

                $slotOffset = $locatorPointerRef + 8 + (15 * 8)
                if (($slotOffset + 8) -gt $Image.Data.Length) {
                    continue
                }

                $slotVa = Read-U64 $Image.Data $slotOffset
                if ($slotVa -lt $Image.ImageBase) {
                    continue
                }

                $slotRva = [uint32]($slotVa - $Image.ImageBase)
                return [pscustomobject]@{
                    Success   = ($slotRva -eq $ExpectedRva)
                    VTableRva = $vtableRva
                    SlotRva   = $slotRva
                }
            }
        }
    }

    [pscustomobject]@{
        Success   = $false
        VTableRva = [uint32]0
        SlotRva   = [uint32]0
    }
}

function Get-Exports($Image) {
    $exports = @{}
    if ($Image.ExportRva -eq 0) {
        return $exports
    }

    $exportOffset = Convert-RvaToOffset $Image $Image.ExportRva
    $numberOfNames = Read-U32 $Image.Data ($exportOffset + 24)
    $addressOfFunctions = Read-U32 $Image.Data ($exportOffset + 28)
    $addressOfNames = Read-U32 $Image.Data ($exportOffset + 32)
    $addressOfNameOrdinals = Read-U32 $Image.Data ($exportOffset + 36)
    $functionsOffset = Convert-RvaToOffset $Image $addressOfFunctions
    $namesOffset = Convert-RvaToOffset $Image $addressOfNames
    $ordinalsOffset = Convert-RvaToOffset $Image $addressOfNameOrdinals

    for ($i = 0; $i -lt $numberOfNames; $i++) {
        $nameRva = Read-U32 $Image.Data ($namesOffset + ($i * 4))
        $nameOffset = Convert-RvaToOffset $Image $nameRva
        $name = Read-AsciiZ $Image.Data $nameOffset
        $ordinal = Read-U16 $Image.Data ($ordinalsOffset + ($i * 2))
        $functionRva = Read-U32 $Image.Data ($functionsOffset + ($ordinal * 4))
        $exports[$name] = [uint32]$functionRva
    }

    $exports
}

function Test-Prefix($Image, [uint32]$Rva, [int[]]$Prefix) {
    $offset = Convert-RvaToOffset $Image $Rva
    if (($offset + $Prefix.Length) -gt $Image.Data.Length) {
        return $false
    }

    for ($i = 0; $i -lt $Prefix.Length; $i++) {
        if ($Image.Data[$offset + $i] -ne $Prefix[$i]) {
            return $false
        }
    }

    $true
}

$readStringAcStringPrefix = @(0x48,0x89,0x5C,0x24,0x18,0x55,0x56,0x57,0x41,0x56,0x41,0x57,0x48,0x8B,0xEC,0x48,0x83,0xEC,0x60)
$readStringAcStringLegacyPrefix = @(0x48,0x89,0x5C,0x24,0x18,0x57,0x48,0x83,0xEC,0x30)
$readStringAcString2027Prefix = @(0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x81,0xEC,0x10,0x01,0x00,0x00)
$readStringWidePrefix = @(0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x81,0xEC,0x00,0x01,0x00,0x00)
$readStringWideLegacyPrefix = @(0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x81,0xEC,0xE0,0x00,0x00,0x00)
$dwgInPrefix2022 = @(0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x81,0xEC,0x40,0x01,0x00,0x00)
$dwgInPrefix2024 = @(0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x81,0xEC,0x60,0x01,0x00,0x00)
$dwgInPrefix2027 = @(0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x81,0xEC,0x30,0x02,0x00,0x00)
$dwgInPrefix = @(0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x81,0xEC,0x00,0x02,0x00,0x00)
$wideAssignPrefix = @(0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xEC,0x20)
$multiByteCifPrefix = @(0x48,0x89,0x5C,0x24,0x10,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56)
$multiByteCif2027Prefix = @(0x48,0x8B,0xC4,0x48,0x89,0x58,0x10,0x55,0x56,0x57,0x41,0x54,0x41,0x55)
$dtextFullInputPrefix = @(0x48,0x85,0xD2,0x74,0x3D,0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x74,0x24,0x10)
$readDoubleBytePrefix = @(0x48,0x89,0x5C,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x57,0x48,0x83,0xEC,0x30)
$readDoubleByteLegacyPrefix = @(0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xEC,0x30)
$multiByteToUnicodePrefix = @(0x48,0x8B,0xC4,0x48,0x89,0x58,0x08,0x48,0x89,0x68,0x10,0x48,0x89,0x70,0x18,0x48,0x89,0x78,0x20,0x41,0x56,0x48,0x83,0xEC,0x30)
$codePageFamilyPrefix = @(0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x57,0x48,0x83,0xEC,0x20)

$readStringAcStringExport = "?readString@AcDbMemoryDwgFiler@@UEAA?AW4ErrorStatus@Acad@@AEAVAcString@@@Z"
$readStringWideExport = "?readString@AcDbMemoryDwgFiler@@UEAA?AW4ErrorStatus@Acad@@PEAPEA_W@Z"
$getFilerCodePageExport = "?acdbGetFilerCodePageId@@YA?AW4code_page_id@@PEAVAcDbDwgFiler@@@Z"
$isDoubleByteExport = "?CodePageIdIsDoubleByte@AcCodePage@@SA_NW4code_page_id@@@Z"
$multiByteCifExport = "?MultiByteCIFToWideChar@@YAHW4code_page_id@@W4MB2Uni@@PEBDHPEA_WH@Z"
$readDoubleByteExport = "?read_doublebyte@TextEditor@@CA_NPEBDAEA_WW4code_page_id@@@Z"
$multiByteToUnicodeExport = "?MultiByteToUnicode@TextEditor@@SA_NPEBDHW4code_page_id@@AEAVAcString@@@Z"

$profiles = @{
    "2022" = @(
        @{ Name="readString AcString"; Kind="Export"; Export=$readStringAcStringExport; Rva=0x37204; Prefix=$readStringAcStringLegacyPrefix },
        @{ Name="readString wchar**"; Kind="Export"; Export=$readStringWideExport; Rva=0x37290; Prefix=$readStringWideLegacyPrefix },
        @{ Name="acdbGetFilerCodePageId"; Kind="Export"; Export=$getFilerCodePageExport; Rva=0x3C6024; Prefix=@(0x48,0x83,0xEC,0x28) },
        @{ Name="CodePageIdIsDoubleByte"; Kind="Export"; Export=$isDoubleByteExport; Rva=0xACAAA8; Prefix=@() },
        @{ Name="AcDbImpText::dwgInFields"; Kind="Rva"; Rva=0x353B0; Prefix=$dwgInPrefix2022 },
        @{ Name="MultiByteCIFToWideChar"; Kind="Export"; Export=$multiByteCifExport; Rva=0x116D70; Prefix=$multiByteCifPrefix },
        @{ Name="DText full input"; Kind="Rva"; Rva=0x6286BC; Prefix=$dtextFullInputPrefix },
        @{ Name="TextEditor::read_doublebyte"; Kind="Export"; Export=$readDoubleByteExport; Rva=0x62A440; Prefix=$readDoubleByteLegacyPrefix },
        @{ Name="TextEditor::MultiByteToUnicode"; Kind="Export"; Export=$multiByteToUnicodeExport; Rva=0x605164; Prefix=$multiByteToUnicodePrefix },
        @{ Name="code-page family context"; Kind="Rva"; Rva=0x626EF8; Prefix=$codePageFamilyPrefix }
    )
    "2023" = @(
        @{ Name="readString AcString"; Kind="Export"; Export=$readStringAcStringExport; Rva=0x662EC; Prefix=$readStringAcStringLegacyPrefix },
        @{ Name="readString wchar**"; Kind="Export"; Export=$readStringWideExport; Rva=0x66700; Prefix=$readStringWideLegacyPrefix },
        @{ Name="acdbGetFilerCodePageId"; Kind="Export"; Export=$getFilerCodePageExport; Rva=0x3A95C0; Prefix=@(0x48,0x83,0xEC,0x28) },
        @{ Name="CodePageIdIsDoubleByte"; Kind="Export"; Export=$isDoubleByteExport; Rva=0xAD9644; Prefix=@() },
        @{ Name="AcDbImpText::dwgInFields"; Kind="Rva"; Rva=0x32B80; Prefix=$dwgInPrefix2022 },
        @{ Name="MultiByteCIFToWideChar"; Kind="Export"; Export=$multiByteCifExport; Rva=0xED7AC; Prefix=$multiByteCifPrefix },
        @{ Name="DText full input"; Kind="Rva"; Rva=0x613464; Prefix=$dtextFullInputPrefix },
        @{ Name="TextEditor::read_doublebyte"; Kind="Export"; Export=$readDoubleByteExport; Rva=0x6151F0; Prefix=$readDoubleByteLegacyPrefix },
        @{ Name="TextEditor::MultiByteToUnicode"; Kind="Export"; Export=$multiByteToUnicodeExport; Rva=0x5EFE38; Prefix=$multiByteToUnicodePrefix },
        @{ Name="code-page family context"; Kind="Rva"; Rva=0x611CB0; Prefix=$codePageFamilyPrefix }
    )
    "2024" = @(
        @{ Name="readString AcString"; Kind="Export"; Export=$readStringAcStringExport; Rva=0x5AB0C; Prefix=$readStringAcStringLegacyPrefix },
        @{ Name="readString wchar**"; Kind="Export"; Export=$readStringWideExport; Rva=0x5AB90; Prefix=$readStringWideLegacyPrefix },
        @{ Name="acdbGetFilerCodePageId"; Kind="Export"; Export=$getFilerCodePageExport; Rva=0x3DD32C; Prefix=@(0x48,0x83,0xEC,0x28) },
        @{ Name="CodePageIdIsDoubleByte"; Kind="Export"; Export=$isDoubleByteExport; Rva=0xAF0294; Prefix=@() },
        @{ Name="AcDbImpText::dwgInFields"; Kind="Rva"; Rva=0x7F730; Prefix=$dwgInPrefix2024 },
        @{ Name="MultiByteCIFToWideChar"; Kind="Export"; Export=$multiByteCifExport; Rva=0x8E888; Prefix=$multiByteCifPrefix },
        @{ Name="DText full input"; Kind="Rva"; Rva=0x646DBC; Prefix=$dtextFullInputPrefix },
        @{ Name="TextEditor::read_doublebyte"; Kind="Export"; Export=$readDoubleByteExport; Rva=0x648B40; Prefix=$readDoubleByteLegacyPrefix },
        @{ Name="TextEditor::MultiByteToUnicode"; Kind="Export"; Export=$multiByteToUnicodeExport; Rva=0x6237A0; Prefix=$multiByteToUnicodePrefix },
        @{ Name="code-page family context"; Kind="Rva"; Rva=0x6455F4; Prefix=$codePageFamilyPrefix }
    )
    "2025" = @(
        @{ Name="readString AcString"; Kind="Export"; Export=$readStringAcStringExport; Rva=0x8C738; Prefix=$readStringAcStringPrefix },
        @{ Name="readString wchar**"; Kind="Export"; Export=$readStringWideExport; Rva=0x8C830; Prefix=$readStringWidePrefix },
        @{ Name="acdbGetFilerCodePageId"; Kind="Export"; Export=$getFilerCodePageExport; Rva=0x43B100; Prefix=@(0x48,0x83,0xEC,0x28) },
        @{ Name="CodePageIdIsDoubleByte"; Kind="Export"; Export=$isDoubleByteExport; Rva=0xBC02EC; Prefix=@() },
        @{ Name="AcDbImpText::dwgInFields"; Kind="Rva"; Rva=0x49910; Prefix=$dwgInPrefix },
        @{ Name="AcString wide assign"; Kind="Rva"; Rva=0x4EF44; Prefix=$wideAssignPrefix; Optional=$true },
        @{ Name="MultiByteCIFToWideChar"; Kind="Export"; Export=$multiByteCifExport; Rva=0x12965C; Prefix=$multiByteCifPrefix },
        @{ Name="DText full input"; Kind="Rva"; Rva=0x6D1660; Prefix=$dtextFullInputPrefix },
        @{ Name="TextEditor::read_doublebyte"; Kind="Export"; Export=$readDoubleByteExport; Rva=0x6D32A4; Prefix=$readDoubleBytePrefix },
        @{ Name="TextEditor::MultiByteToUnicode"; Kind="Export"; Export=$multiByteToUnicodeExport; Rva=0x6ACF18; Prefix=$multiByteToUnicodePrefix },
        @{ Name="code-page family context"; Kind="Rva"; Rva=0x6CFE6C; Prefix=$codePageFamilyPrefix }
    )
    "2026" = @(
        @{ Name="readString AcString"; Kind="Export"; Export=$readStringAcStringExport; Rva=0x93E70; Prefix=$readStringAcStringPrefix },
        @{ Name="readString wchar**"; Kind="Export"; Export=$readStringWideExport; Rva=0x93F60; Prefix=$readStringWidePrefix },
        @{ Name="acdbGetFilerCodePageId"; Kind="Export"; Export=$getFilerCodePageExport; Rva=0x4478E0; Prefix=@(0x48,0x83,0xEC,0x28) },
        @{ Name="CodePageIdIsDoubleByte"; Kind="Export"; Export=$isDoubleByteExport; Rva=0xBCF310; Prefix=@() },
        @{ Name="AcDbImpText::dwgInFields"; Kind="Rva"; Rva=0x33690; Prefix=$dwgInPrefix },
        @{ Name="MultiByteCIFToWideChar"; Kind="Export"; Export=$multiByteCifExport; Rva=0xD71A8; Prefix=$multiByteCifPrefix },
        @{ Name="DText full input"; Kind="Rva"; Rva=0x6DE5B0; Prefix=$dtextFullInputPrefix },
        @{ Name="TextEditor::read_doublebyte"; Kind="Export"; Export=$readDoubleByteExport; Rva=0x6E01F8; Prefix=$readDoubleBytePrefix },
        @{ Name="TextEditor::MultiByteToUnicode"; Kind="Export"; Export=$multiByteToUnicodeExport; Rva=0x6B9F04; Prefix=$multiByteToUnicodePrefix },
        @{ Name="code-page family context"; Kind="Rva"; Rva=0x6DCDC0; Prefix=$codePageFamilyPrefix }
    )
    "2027" = @(
        @{ Name="readString AcString"; Kind="Export"; Export=$readStringAcStringExport; Rva=0x4EF70; Prefix=$readStringAcString2027Prefix },
        @{ Name="acdbGetFilerCodePageId"; Kind="Export"; Export=$getFilerCodePageExport; Rva=0x4579A4; Prefix=@(0x48,0x83,0xEC,0x28) },
        @{ Name="CodePageIdIsDoubleByte"; Kind="Export"; Export=$isDoubleByteExport; Rva=0xBF2344; Prefix=@() },
        @{ Name="AcDbImpText::dwgInFields"; Kind="Rva"; Rva=0x2E740; Prefix=$dwgInPrefix2027 },
        @{ Name="MultiByteCIFToWideChar"; Kind="Export"; Export=$multiByteCifExport; Rva=0x140220; Prefix=$multiByteCif2027Prefix },
        @{ Name="DText full input"; Kind="Rva"; Rva=0x6E90DC; Prefix=$dtextFullInputPrefix },
        @{ Name="TextEditor::read_doublebyte"; Kind="Export"; Export=$readDoubleByteExport; Rva=0x6EAD14; Prefix=$readDoubleBytePrefix },
        @{ Name="TextEditor::MultiByteToUnicode"; Kind="Export"; Export=$multiByteToUnicodeExport; Rva=0x6C4A0C; Prefix=$multiByteToUnicodePrefix },
        @{ Name="code-page family context"; Kind="Rva"; Rva=0x6E79A0; Prefix=$codePageFamilyPrefix }
    )
}

$acDbImpTextDwgInFieldsVTableSlot15 = @{
    "2018" = 0x3717C0
    "2019" = 0x04F800
    "2020" = 0x073F10
    "2021" = 0x0835A0
    "2022" = 0x0353B0
    "2023" = 0x032B80
    "2024" = 0x07F730
    "2025" = 0x049910
    "2026" = 0x033690
    "2027" = 0x02E740
}

$failClosedVersions = @("2018", "2019", "2020", "2021")
$failures = 0

Get-ChildItem -LiteralPath $ChorePath -Filter "acdb*#*.dll" | Sort-Object Name | ForEach-Object {
    if ($_.Name -notmatch "#(?<version>\d{4})\.dll$") {
        return
    }

    $version = $Matches.version
    $image = Get-PeImage $_.FullName
    $exports = Get-Exports $image
    Write-Host "[$version] $($_.Name)"

    if ($acDbImpTextDwgInFieldsVTableSlot15.ContainsKey($version)) {
        $expectedDwgInFieldsRva = [uint32]$acDbImpTextDwgInFieldsVTableSlot15[$version]
        $vtableCheck = Test-AcDbImpTextVTableSlot $image $expectedDwgInFieldsRva
        if (-not $vtableCheck.Success) {
            Write-Host "  ERROR: AcDbImpText.vtbl[15] expected 0x$($expectedDwgInFieldsRva.ToString('X')), actual 0x$(([uint32]$vtableCheck.SlotRva).ToString('X'))"
            $script:failures++
        } else {
            Write-Host "  OK: AcDbImpText.vtbl[15] -> 0x$(([uint32]$vtableCheck.SlotRva).ToString('X'))"
        }
    }

    if ($failClosedVersions -contains $version) {
        Write-Host "  fail-closed profile: no DBText native hook target is required."
        return
    }

    if (-not $profiles.ContainsKey($version)) {
        Write-Host "  ERROR: no profile verification rule."
        $script:failures++
        return
    }

    foreach ($target in $profiles[$version]) {
        $actualRva = [uint32]0
        if ($target.Kind -eq "Export") {
            if (-not $exports.ContainsKey($target.Export)) {
                Write-Host "  ERROR: missing export $($target.Name)"
                $script:failures++
                continue
            }

            $actualRva = [uint32]$exports[$target.Export]
        } else {
            $actualRva = [uint32]$target.Rva
        }

        if ($actualRva -ne [uint32]$target.Rva) {
            Write-Host "  ERROR: $($target.Name) RVA expected 0x$(([uint32]$target.Rva).ToString('X')), actual 0x$($actualRva.ToString('X'))"
            $script:failures++
            continue
        }

        if ($target.Prefix.Count -gt 0 -and -not (Test-Prefix $image $actualRva $target.Prefix)) {
            Write-Host "  ERROR: $($target.Name) prefix mismatch at 0x$($actualRva.ToString('X'))"
            $script:failures++
            continue
        }

        Write-Host "  OK: $($target.Name) 0x$($actualRva.ToString('X'))"
    }
}

if ($failures -ne 0) {
    throw "Native decode hook profile verification failed: $failures"
}

Write-Host "Native decode hook profile verification passed."
