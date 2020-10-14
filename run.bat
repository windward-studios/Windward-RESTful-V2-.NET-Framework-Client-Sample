set "pathMSBuild=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin"
set "execpath=%pathMSBuild%\MSBuild.exe"
dotnet restore & "%execpath%" RunReport.sln & bin\Debug\net472\RunReport.exe files\Manufacturing.docx files\output.docx -xml:MANF_DATA_2009 files\Manufacturing.xml & pause