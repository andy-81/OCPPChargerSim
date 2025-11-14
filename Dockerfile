FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY OCPPChargerSim/OCPPChargerSim.csproj OCPPChargerSim/
RUN dotnet restore OCPPChargerSim/OCPPChargerSim.csproj

COPY . .
RUN dotnet publish OCPPChargerSim/OCPPChargerSim.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

COPY --from=build /app/out ./

ENV APP_DATA=/app/data
ENV ASPNETCORE_URLS=http://+:5000
RUN mkdir -p "$APP_DATA"
EXPOSE 5000

ENTRYPOINT ["dotnet", "OCPPChargerSim.dll"]
