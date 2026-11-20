#!/usr/bin/env bash
# 폰 화면·받기·쓰는 법 쪽과 PC 프로그램을 Pages 프로젝트 stream 에 올린다.
# PC 프로그램은 pc/out/리모컨.exe 를 그대로 복사(설정 폴더는 올리지 않음):
#   remote.exe  — 실행 파일
#   remote.zip  — 리모컨.exe 하나를 담은 zip(이름은 UTF-8로, 탐색기 「모두 추출」이면 리모컨 폴더가 된다)
set -e
cd "$(dirname "$0")"
cp ../pc/out/리모컨.exe public/remote.exe
powershell -NoProfile -Command "
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  \$zip = (Resolve-Path 'public').Path + '\remote.zip'
  if (Test-Path \$zip) { Remove-Item \$zip }
  \$z = [IO.Compression.ZipFile]::Open(\$zip, 'Create')
  [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(\$z, (Resolve-Path '../pc/out/리모컨.exe').Path, '리모컨.exe')
  \$z.Dispose()
"
../wr.sh pages deploy --project-name stream --branch main --commit-dirty=true
