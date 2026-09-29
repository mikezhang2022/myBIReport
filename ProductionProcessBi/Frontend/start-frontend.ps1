param([int]$Port = 5173, [string]$ApiBase = 'http://127.0.0.1:5095')

python (Join-Path $PSScriptRoot 'server.py') --port $Port --api-base $ApiBase
