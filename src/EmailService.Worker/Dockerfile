FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY EmailService.Contracts ./EmailService.Contracts
COPY EmailService.Infrastructure ./EmailService.Infrastructure
COPY EmailService.Worker ./EmailService.Worker

RUN dotnet restore ./EmailService.Worker/EmailService.Worker.csproj
RUN dotnet publish ./EmailService.Worker/EmailService.Worker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "EmailService.Worker.dll"]
