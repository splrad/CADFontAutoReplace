#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,

    [Parameter(Mandatory = $true)]
    [string]$TagName,

    [string]$PreviousTag = '',

    [Parameter(Mandatory = $true)]
    [string]$TargetSha,

    [Parameter(Mandatory = $true)]
    [string]$TriggerPrNumber,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$OwnerLogin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-GhJson([string[]]$Arguments) {
    $output = & gh api @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "gh api failed: gh api $($Arguments -join ' '): $($output -join "`n")"
    }

    $json = ($output -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($json)) {
        return @()
    }

    return $json | ConvertFrom-Json -Depth 100
}

function Invoke-Git([string[]]$Arguments) {
    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git failed: git $($Arguments -join ' '): $($output -join "`n")"
    }

    return @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Test-GitCommitExists([string]$Revision) {
    if ([string]::IsNullOrWhiteSpace($Revision)) {
        return $false
    }

    & git rev-parse --verify "$Revision^{commit}" *> $null
    return $LASTEXITCODE -eq 0
}

function Get-ReleaseCommitShas {
    if (-not (Test-GitCommitExists $TargetSha)) {
        throw "Target commit does not exist locally: $TargetSha"
    }

    $range = $TargetSha
    if (Test-GitCommitExists $PreviousTag) {
        $range = "$PreviousTag..$TargetSha"
    }

    return Invoke-Git @('rev-list', '--first-parent', '--reverse', $range)
}

function Get-PullRequestFiles([int]$Number) {
    $files = New-Object System.Collections.Generic.List[string]
    $page = 1

    do {
        $batch = @(Invoke-GhJson @(
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            "repos/$Repo/pulls/$Number/files?per_page=100&page=$page"
        ))

        foreach ($file in $batch) {
            if ($file.filename) {
                $files.Add([string]$file.filename)
            }
        }

        $page++
    } while ($batch.Count -eq 100)

    return @($files)
}

function Get-PullRequestModel([int]$Number) {
    $pull = Invoke-GhJson @(
        '-H', 'Accept: application/vnd.github+json',
        '-H', 'X-GitHub-Api-Version: 2022-11-28',
        "repos/$Repo/pulls/$Number"
    )

    $labels = @()
    if ($pull.labels) {
        $labels = @($pull.labels | ForEach-Object { [string]$_.name })
    }

    [pscustomobject]@{
        Number   = [int]$pull.number
        Title    = [string]$pull.title
        Author   = [string]$pull.user.login
        MergedAt = [string]$pull.merged_at
        Labels   = $labels
        Files    = @(Get-PullRequestFiles -Number $Number)
    }
}

function Get-AssociatedPullRequestNumbers {
    $numbers = [ordered]@{}

    foreach ($sha in Get-ReleaseCommitShas) {
        $pulls = @(Invoke-GhJson @(
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            "repos/$Repo/commits/$sha/pulls"
        ))

        foreach ($pull in $pulls) {
            if ($pull.state -eq 'closed' -and $pull.merged_at -and -not $numbers.Contains([string]$pull.number)) {
                $numbers.Add([string]$pull.number, [int]$pull.number)
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($TriggerPrNumber)) {
        $triggerNumber = [int]$TriggerPrNumber
        if (-not $numbers.Contains([string]$triggerNumber)) {
            $numbers.Add([string]$triggerNumber, $triggerNumber)
        }
    }

    return @($numbers.Values)
}

function Normalize-RepoPath([string]$Path) {
    return ($Path.Replace('\', '/') -replace '^\./', '')
}

function Test-RuntimeReleasePath([string]$Path) {
    $normalized = Normalize-RepoPath $Path
    $lower = $normalized.ToLowerInvariant()

    if ($lower.StartsWith('.github/') -or
        $lower.StartsWith('.local/') -or
        $lower.StartsWith('.agents/') -or
        $lower.StartsWith('.codex/') -or
        $lower.StartsWith('.claude/') -or
        $lower.StartsWith('.vscode/') -or
        $lower.StartsWith('docs/') -or
        $lower -eq 'readme.md' -or
        $lower -eq 'license.txt' -or
        $lower -eq 'version.props' -or
        $lower -eq 'cadfontautoreplace.slnx' -or
        $lower -eq '.editorconfig' -or
        $lower -eq '.gitattributes' -or
        $lower -eq '.gitignore') {
        return $false
    }

    if ($lower.StartsWith('src/')) {
        return $true
    }

    if ($lower -eq 'tools/publish-releaseassets.ps1') {
        return $true
    }

    if ($lower -in @('directory.build.props', 'directory.build.targets', 'directory.packages.props', 'global.json')) {
        return $true
    }

    if ($lower -eq 'chore/fonts.zip') {
        return $true
    }

    return $false
}

function Test-InstallOrPackagePath([string]$Path) {
    $lower = (Normalize-RepoPath $Path).ToLowerInvariant()
    return $lower -eq 'tools/publish-releaseassets.ps1' -or
        $lower -eq 'chore/fonts.zip' -or
        $lower -in @('directory.build.props', 'directory.build.targets', 'directory.packages.props', 'global.json')
}

function Test-ReleaseNotesExcluded($PullRequest) {
    $excludedLabels = @('ignore-for-release', 'skip-changelog', 'no-changelog', 'no-release-notes')
    $labels = @($PullRequest.Labels | ForEach-Object { $_.ToLowerInvariant() })
    return @($labels | Where-Object { $excludedLabels -contains $_ }).Count -gt 0
}

function Test-IncludedPullRequest($PullRequest) {
    if (Test-ReleaseNotesExcluded $PullRequest) {
        return $false
    }

    return @($PullRequest.Files | Where-Object { Test-RuntimeReleasePath $_ }).Count -gt 0
}

function Test-BotLogin([string]$Login) {
    if ([string]::IsNullOrWhiteSpace($Login)) {
        return $true
    }

    $lower = $Login.ToLowerInvariant()
    return $lower.EndsWith('[bot]') -or $lower -in @('dependabot', 'github-actions')
}

function Get-ChangeCategory($PullRequest) {
    $labels = @($PullRequest.Labels | ForEach-Object { $_.ToLowerInvariant() })
    $text = (@($PullRequest.Title) + $labels) -join "`n"

    if ($labels -contains 'breaking-change' -or $labels -contains 'breaking' -or $labels -contains 'semver-major' -or
        $text -match '(breaking|破坏性|不兼容)') {
        return '破坏性变更'
    }

    if ($labels -contains 'security' -or $labels -contains 'vulnerability' -or $text -match '(security|安全|漏洞|cve)') {
        return '安全修复'
    }

    if ($labels -contains 'feature' -or $labels -contains 'feat' -or $labels -contains 'enhancement' -or $labels -contains 'semver-minor' -or
        $text -match '(^|\s|\n)(feat|feature|enhancement)(\([a-z0-9-]+\))?!?:' -or
        $text -match '(新增|添加|功能)') {
        return '新增功能'
    }

    if ($labels -contains 'bug' -or $labels -contains 'fix' -or $labels -contains 'bugfix' -or $labels -contains 'regression' -or
        $text -match '(^|\s|\n)(fix|bug|bugfix|regression)(\([a-z0-9-]+\))?!?:' -or
        $text -match '(修复|缺陷|问题)') {
        return '问题修复'
    }

    if ($labels -contains 'performance' -or $labels -contains 'perf' -or
        $text -match '(^|\s|\n)(perf|performance)(\([a-z0-9-]+\))?!?:' -or
        $text -match '性能') {
        return '性能优化'
    }

    if ($labels -contains 'build' -or
        $text -match '(^|\s|\n)build(\([a-z0-9-]+\))?!?:' -or
        @($PullRequest.Files | Where-Object { Test-InstallOrPackagePath $_ }).Count -gt 0) {
        return '安装与发布包'
    }

    return '其他插件变更'
}

function Normalize-Title([string]$Title) {
    return ($Title -replace '\s+', ' ').Trim()
}

function New-ChangeLine($PullRequest) {
    $title = Normalize-Title $PullRequest.Title
    return "- $title by @$($PullRequest.Author) in #$($PullRequest.Number)"
}

function New-ContributorAvatar([string]$Login) {
    $safeLogin = [System.Net.WebUtility]::HtmlEncode($Login)
    return "<a href=`"https://github.com/$safeLogin`" title=`"@$safeLogin`"><img src=`"https://github.com/$safeLogin.png?size=64`" width=`"48`" height=`"48`" alt=`"@$safeLogin`" /></a>"
}

function New-ReleaseBody($PullRequests) {
    $categoryOrder = @(
        '破坏性变更',
        '安全修复',
        '新增功能',
        '问题修复',
        '性能优化',
        '安装与发布包',
        '其他插件变更'
    )
    $categories = [ordered]@{}
    foreach ($category in $categoryOrder) {
        $categories[$category] = New-Object System.Collections.Generic.List[string]
    }

    $contributors = [ordered]@{}
    $ownerLower = $OwnerLogin.ToLowerInvariant()

    foreach ($pr in $PullRequests) {
        $category = Get-ChangeCategory $pr
        $categories[$category].Add((New-ChangeLine $pr))

        $authorLower = $pr.Author.ToLowerInvariant()
        if ($authorLower -ne $ownerLower -and -not (Test-BotLogin $pr.Author) -and -not $contributors.Contains($authorLower)) {
            $contributors.Add($authorLower, $pr.Author)
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $hasChanges = $false
    foreach ($category in $categoryOrder) {
        if ($categories[$category].Count -eq 0) {
            continue
        }

        $hasChanges = $true
        $lines.Add("## $category")
        $lines.Add('')
        foreach ($line in $categories[$category]) {
            $lines.Add($line)
        }
        $lines.Add('')
    }

    if (-not $hasChanges) {
        $lines.Add('## 插件变更')
        $lines.Add('')
        $lines.Add('- 本版本无用户可见运行代码变化。')
        $lines.Add('')
    }

    if ($contributors.Count -gt 0) {
        $lines.Add('## 贡献者')
        $lines.Add('')
        $lines.Add((@($contributors.Values | ForEach-Object { New-ContributorAvatar $_ }) -join ' '))
        $lines.Add('')
    }

    return (($lines | ForEach-Object { [string]$_ }) -join "`n").Trim()
}

$pullRequests = @(
    Get-AssociatedPullRequestNumbers |
        ForEach-Object { Get-PullRequestModel -Number $_ } |
        Where-Object { $_.MergedAt } |
        Sort-Object @{ Expression = { [DateTime]$_.MergedAt } }, Number
)
$includedPullRequests = @($pullRequests | Where-Object { Test-IncludedPullRequest $_ })
$generatedBody = New-ReleaseBody -PullRequests $includedPullRequests

$lines = @(
    '## 下载说明',
    '',
    '| 文件 | 说明 |',
    '| --- | --- |',
    "| **AFR-Deployer_$TagName.exe** | 主安装程序，双击运行并选择 AutoCAD 版本 |",
    "| AFR-DLL_$TagName.zip | 手动 NETLOAD 用插件 DLL 包 |",
    '| Fonts.zip | 字体资源包（用于手动补充或备份） |',
    '',
    "一般用户只需下载：**AFR-Deployer_$TagName.exe**",
    '',
    '------',
    '',
    $generatedBody,
    '',
    '------',
    '',
    '## 升级说明',
    '',
    '- 支持直接覆盖安装',
    '- 无需卸载旧版本',
    '- 已安装字体不会被删除'
)

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$lines | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Generated release notes at $OutputPath from $($includedPullRequests.Count) runtime PR(s)."
