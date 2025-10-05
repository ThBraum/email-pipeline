FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY EmailService.Contracts ./EmailService.Contracts
COPY EmailService.Infrastructure ./EmailService.Infrastructure
COPY EmailService.Api ./EmailService.Api

RUN dotnet restore ./EmailService.Api/EmailService.Api.csproj
RUN dotnet publish ./EmailService.Api/EmailService.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "EmailService.Api.dll"]
