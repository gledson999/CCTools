using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CCTools
{
    // Estrutura para representar uma entrada no arquivo FSE (12 bytes)
    struct FseEntry
    {
        public uint OffsetSector; // Multiplicar por 2048 para ter o offset real
        public uint Size;         // Tamanho real do arquivo
        public uint Padding;      // 4 bytes finais (geralmente 0)
        public bool IsNull;       // Flag para identificar se a entrada é 0000...

        // Método para ler a entrada do arquivo
        public static FseEntry Read(BinaryReader reader)
        {
            FseEntry entry = new FseEntry();
            entry.OffsetSector = reader.ReadUInt32();
            entry.Size = reader.ReadUInt32();
            entry.Padding = reader.ReadUInt32();

            // Verifica se é uma entrada nula (00 00 00 00 00 00 00 00 00 00 00 00)
            entry.IsNull = (entry.OffsetSector == 0 && entry.Size == 0 && entry.Padding == 0);
            return entry;
        }

        // Método para escrever a entrada no arquivo
        public void Write(BinaryWriter writer)
        {
            writer.Write(OffsetSector);
            writer.Write(Size);
            writer.Write(Padding);
        }
    }

    class Program
    {
        const int SECTOR_SIZE = 2048; // 0x800

        static void Main(string[] args)
        {
            Console.WriteLine("Crisis Core: Final Fantasy VII - PKG/FSE Extractor/Repacker");
            Console.WriteLine("-----------------------------------------------------------");

            if (args.Length < 3)
            {
                ShowUsage();
                return;
            }

            string mode = args[0];
            string fsePath = args[1];
            string folderPath = args[2];

            try
            {
                if (mode == "-u")
                {
                    Extract(fsePath, folderPath);
                }
                else if (mode == "-r")
                {
                    Repack(fsePath, folderPath);
                }
                else
                {
                    ShowUsage();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[ERROR]: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static void ShowUsage()
        {
            Console.WriteLine("Use:");
            Console.WriteLine("  Extract:     CCTools.exe -u <File.fse> <Folder>");
            Console.WriteLine("  Repack:      CCTools.exe -r <File.fse> <Folder>");
        }

        // --- LÓGICA DE EXTRAÇÃO ---
        static void Extract(string fsePath, string outputFolder)
        {
            string pkgPath = Path.ChangeExtension(fsePath, ".pkg");

            if (!File.Exists(fsePath)) throw new FileNotFoundException("FSE file not found.");
            if (!File.Exists(pkgPath)) throw new FileNotFoundException("PKG file not found.");

            if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

            Console.WriteLine($"Reading {Path.GetFileName(fsePath)}...");

            using (FileStream fsFse = new FileStream(fsePath, FileMode.Open, FileAccess.Read))
            using (FileStream fsPkg = new FileStream(pkgPath, FileMode.Open, FileAccess.Read))
            using (BinaryReader brFse = new BinaryReader(fsFse))
            using (BinaryReader brPkg = new BinaryReader(fsPkg))
            {
                int index = 0;
                while (fsFse.Position < fsFse.Length)
                {
                    // Lê 12 bytes
                    if (fsFse.Length - fsFse.Position < 12) break;
                    FseEntry entry = FseEntry.Read(brFse);

                    // Se for nulo, apenas incrementa o índice para manter a contagem correta (File000XX)
                    // mas não cria arquivo, conforme lógica padrão de placeholders.
                    // Se desejar criar arquivos vazios, remova o 'continue'.
                    if (entry.IsNull)
                    {
                        // Console.WriteLine($"Index {index}: Entrada Nula (Pular)");
                        index++;
                        continue;
                    }

                    long realOffset = (long)entry.OffsetSector * SECTOR_SIZE;

                    if (realOffset + entry.Size > fsPkg.Length)
                    {
                        Console.WriteLine($"[WARNING] File {index} pointers outside the PKG. Ignoring.");
                        index++;
                        continue;
                    }

                    // Ler dados do PKG
                    fsPkg.Seek(realOffset, SeekOrigin.Begin);
                    byte[] data = brPkg.ReadBytes((int)entry.Size);

                    // Detectar extensão
                    string ext = DetectExtension(data);
                    string fileName = $"File{index:D5}{ext}";
                    string fullPath = Path.Combine(outputFolder, fileName);

                    File.WriteAllBytes(fullPath, data);
                    Console.WriteLine($"Extracted: {fileName}");

                    index++;
                }
            }
            Console.WriteLine("\nExtracted successfully!");
        }

        // --- LÓGICA DE REPACOTAMENTO ---
        static void Repack(string originalFsePath, string inputFolder)
        {
            string dir = Path.GetDirectoryName(originalFsePath);
            string name = Path.GetFileNameWithoutExtension(originalFsePath); // ex: discimg

            string newFsePath = Path.Combine(dir, "new_" + name + ".fse");
            string newPkgPath = Path.Combine(dir, "new_" + name + ".pkg");

            if (!File.Exists(originalFsePath)) throw new FileNotFoundException("Original FSE file required for reference.");

            Console.WriteLine($"Creating {Path.GetFileName(newPkgPath)} based on {Path.GetFileName(originalFsePath)}...");

            using (FileStream fsOldFse = new FileStream(originalFsePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader brOldFse = new BinaryReader(fsOldFse))
            using (FileStream fsNewFse = new FileStream(newFsePath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bwNewFse = new BinaryWriter(fsNewFse))
            using (FileStream fsNewPkg = new FileStream(newPkgPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bwNewPkg = new BinaryWriter(fsNewPkg))
            {
                int index = 0;
                while (fsOldFse.Position < fsOldFse.Length)
                {
                    if (fsOldFse.Length - fsOldFse.Position < 12) break;

                    // Lemos a entrada original para saber se é NULL ou se tem padding específico
                    FseEntry originalEntry = FseEntry.Read(brOldFse);

                    if (originalEntry.IsNull)
                    {
                        // Requisito 4: Manter valores nulos intactos e na mesma posição
                        FseEntry nullEntry = new FseEntry { OffsetSector = 0, Size = 0, Padding = 0, IsNull = true };
                        nullEntry.Write(bwNewFse);
                        // Não escreve nada no PKG para entradas nulas
                        index++;
                        continue;
                    }

                    // Tenta encontrar o arquivo extraído correspondente: FileXXXXX.*
                    string searchPattern = $"File{index:D5}.*";
                    string[] files = Directory.GetFiles(inputFolder, searchPattern);

                    if (files.Length == 0)
                    {
                        Console.WriteLine($"[WARNING] File {searchPattern} not found in folder. Creating an empty entry instead.");
                        // Se o arquivo sumiu, escrevemos zerado para não quebrar a estrutura?
                        // Ou copiamos o original? O ideal no repack é que o arquivo exista.
                        // Aqui vou criar uma entrada vazia por segurança.
                        new FseEntry().Write(bwNewFse);
                    }
                    else
                    {
                        string fileToImport = files[0]; // Pega o primeiro match (ex: File00000.atel)
                        byte[] data = File.ReadAllBytes(fileToImport);

                        // Alinhamento no novo PKG (Múltiplo de 2048)
                        long currentPos = fsNewPkg.Position;
                        long paddingNeeded = 0;

                        if (currentPos % SECTOR_SIZE != 0)
                        {
                            paddingNeeded = SECTOR_SIZE - (currentPos % SECTOR_SIZE);
                            bwNewPkg.Write(new byte[paddingNeeded]);
                        }

                        // Recalcula posição após padding
                        currentPos = fsNewPkg.Position;

                        // Cria nova entrada
                        FseEntry newEntry = new FseEntry();
                        newEntry.OffsetSector = (uint)(currentPos / SECTOR_SIZE);
                        newEntry.Size = (uint)data.Length;
                        newEntry.Padding = originalEntry.Padding; // Mantém o padding original (byte 8-11)
                        newEntry.IsNull = false;

                        // Escreve dados no PKG
                        bwNewPkg.Write(data);

                        // Escreve entrada no FSE
                        newEntry.Write(bwNewFse);

                        Console.WriteLine($"Added: {Path.GetFileName(fileToImport)}");
                    }

                    index++;
                }
            }

            Console.WriteLine("\nRepackaged successfully!");
            Console.WriteLine($"Created: {Path.GetFileName(newFsePath)}");
            Console.WriteLine($"Created: {Path.GetFileName(newPkgPath)}");
        }

        // --- DETECÇÃO DE EXTENSÃO (Requisito 6) ---
        static string DetectExtension(byte[] data)
        {
            if (data.Length < 4) return ".dat";

            // Lê os primeiros 4 bytes como UInt32 Big Endian ou apenas compara bytes
            // A ordem do header no prompt (Ex: 4174656C) sugere leitura direta dos bytes
            // 0x4174656C = 'atel' em ASCII.

            uint header = BitConverter.ToUInt32(data, 0);
            // Nota: BitConverter depende da arquitetura (Little Endian em PC x86/x64).
            // O arquivo parece ser Little Endian, então leremos o int normal.

            // Mapas baseados no prompt (Headers em Hex)
            // Cuidado: Se o prompt diz "Header: 4174656C", precisamos ver como isso fica em Int32
            // Se o arquivo começa com byte 41, depois 74... em Little Endian o int é 0x6C657441.
            // Vou usar comparação de bytes para ser mais seguro e não depender da Endianness da CPU.

            if (IsMagic(data, 0x41, 0x74, 0x65, 0x6C)) return ".atel";
            if (IsMagic(data, 0x50, 0x53, 0x4D, 0x46)) return ".pmf";
            if (IsMagic(data, 0x52, 0x49, 0x46, 0x46)) return ".at3";
            if (IsMagic(data, 0x53, 0x53, 0x43, 0x46)) return ".ssc";
            if (IsMagic(data, 0x89, 0x50, 0x4E, 0x47)) return ".png";
            if (IsMagic(data, 0x4D, 0x42, 0x44, 0x00)) return ".mbd";
            if (IsMagic(data, 0x47, 0x54, 0x46, 0x00)) return ".gtf"; // Prompt diz 47540002 mas geralmente magic é 4 bytes, ajustado conforme pedido
            if (IsMagic(data, 0x47, 0x54, 0x00, 0x02)) return ".gtf"; // Caso o prompt seja literal
            if (IsMagic(data, 0x00, 0x00, 0x00, 0x00)) return ".mdl"; // Estranho header zerado ser MDL, mas seguindo o prompt
            if (IsMagic(data, 0x46, 0x52, 0x52, 0x00)) return ".frr";
            if (IsMagic(data, 0x46, 0x45, 0x50, 0x00)) return ".fep";

            return ".dat"; // Padrão
        }

        static bool IsMagic(byte[] data, byte b0, byte b1, byte b2, byte b3)
        {
            return data[0] == b0 && data[1] == b1 && data[2] == b2 && data[3] == b3;
        }
    }
}