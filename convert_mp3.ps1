$ErrorActionPreference = "Continue"
$ff = "D:\portable\ffmpeg.exe"
$dir = "D:\下载\合集_2026-06-07_改改的演出日记"
$files = Get-ChildItem $dir -Filter *.mp3
$ok = 0; $fail = 0
foreach ($f in $files) {
    # 只转 ftyp 容器的（文件头 4-7 字节 = "ftyp"）
    $fs = [System.IO.File]::OpenRead($f.FullName)
    $b = New-Object byte[] 12
    [void]$fs.Read($b, 0, 12); $fs.Close()
    $isFtyp = ($b[4] -eq 0x66 -and $b[5] -eq 0x74 -and $b[6] -eq 0x79 -and $b[7] -eq 0x70)
    if (-not $isFtyp) { Write-Output ("SKIP(real-mp3) " + $f.Name); continue }

    $tmp = [System.IO.Path]::Combine($dir, ("__conv_" + $f.Name))
    & $ff -y -loglevel error -i $f.FullName -c:a libmp3lame -q:a 2 -vn $tmp
    if ($LASTEXITCODE -eq 0 -and (Test-Path $tmp) -and ((Get-Item $tmp).Length -gt 10000)) {
        Move-Item $tmp $f.FullName -Force
        $ok++
        Write-Output ("OK   " + $f.Name + " -> " + [math]::Round((Get-Item $f.FullName).Length/1MB,1) + "MB")
    } else {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        $fail++
        Write-Output ("FAIL " + $f.Name)
    }
}
Write-Output ("DONE ok=" + $ok + " fail=" + $fail)
