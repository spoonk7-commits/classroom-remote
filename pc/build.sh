#!/usr/bin/env bash
# 교실 리모컨 PC 프로그램 빌드 — 윈도우 기본 .NET Framework 4.8 컴파일러(csc, C# 5)만 쓴다.
# 결과: pc/out/리모컨.exe
set -e
cd "$(dirname "$0")"
CSC=/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe
FW=/c/Windows/Microsoft.NET/Framework64/v4.0.30319
mkdir -p out
# 공식 배포판: 공개 저장소 밖 private/host.txt 가 있으면 기본 중계 주소로 넣는다(코드만 넣어도 되게)
HOSTRES=""; [ -f ../private/host.txt ] && HOSTRES="-resource:../private/host.txt,host.txt"
"$CSC" -nologo -codepage:65001 -target:winexe -optimize+ -out:out/리모컨.exe \
  -win32icon:icon.ico \
  -resource:기본덱.json,deck.json -resource:기본앱단추.json,apps.json $HOSTRES \
  -r:"$FW/System.Web.Extensions.dll" -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:Microsoft.CSharp.dll -r:System.Core.dll \
  *.cs
ls -l out/리모컨.exe
