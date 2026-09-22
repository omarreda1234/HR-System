# 1. استخدام صورة SDK لبناء المشروع
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 2. نسخ ملف المشروع واستعادة الحزم (Restore)
COPY ["HRSystem.csproj", "./"]
RUN dotnet restore "./HRSystem.csproj"

# 3. نسخ باقي الملفات وعمل البناء (Build)
COPY . .
WORKDIR "/src/."
RUN dotnet build "HRSystem.csproj" -c Release -o /app/build

# 4. نشر المشروع (Publish)
FROM build AS publish
RUN dotnet publish "HRSystem.csproj" -c Release -o /app/publish /p:UseAppHost=false

# 5. صورة التشغيل النهائية
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

# 6. تحديد المنفذ (Port) الافتراضي
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "HRSystem.dll"]
