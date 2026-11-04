#!/bin/sh

dotnet new nugetconfig > /dev/null 2>&1
dotnet nuget add source https://pkgs.dev.azure.com/sda-iatec/_packaging/IATec.Community/nuget/v3/index.json -n PrivateFeed1 -u #{Username}# -p #{Password}# --store-password-in-clear-text > /dev/null 2>&1