# TalentLens web app (ASP.NET Core MVC, .NET 10). Build context: the web/ folder.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TalentLens.slnx ./
COPY TalentLens.Domain/TalentLens.Domain.csproj TalentLens.Domain/
COPY TalentLens.Infrastructure/TalentLens.Infrastructure.csproj TalentLens.Infrastructure/
COPY TalentLens.Web/TalentLens.Web.csproj TalentLens.Web/
RUN dotnet restore TalentLens.Web/TalentLens.Web.csproj

COPY TalentLens.Domain/ TalentLens.Domain/
COPY TalentLens.Infrastructure/ TalentLens.Infrastructure/
COPY TalentLens.Web/ TalentLens.Web/
RUN dotnet publish TalentLens.Web/TalentLens.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME ["/app/App_Data"]
ENTRYPOINT ["dotnet", "TalentLens.Web.dll"]
