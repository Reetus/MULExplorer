using System.Runtime.InteropServices;

namespace UODataAccess.IO;

// Burrows-Wheeler decoder used by the modern (CV >= 7.0.10.4) gumpart UOP
// after the zlib step. Direct algorithmic port from ClassicUO.Utility.BWTDecompress
// — pure managed code, no XNA dependency.
public static class BWTDecompress
{
    public static byte[] Decompress( byte[] buffer )
    {
        using var ms = new MemoryStream( buffer );
        using var reader = new BinaryReader( ms );

        var header = reader.ReadUInt32();
        var firstChar = reader.ReadByte();

        Span<ushort> table = new ushort[256 * 256];
        BuildTable( table, firstChar );

        var list = new byte[reader.BaseStream.Length - 4];
        var i = 0;

        while ( reader.BaseStream.Position < reader.BaseStream.Length )
        {
            var current = firstChar;
            var value = table[current];

            if ( current > 0 )
            {
                do { table[current] = table[current - 1]; }
                while ( --current > 0 );
            }

            table[0] = value;
            list[i++] = (byte) value;
            firstChar = reader.ReadByte();
        }

        return InternalDecompress( list, 0u );
    }

    private static void BuildTable( Span<ushort> table, byte startValue )
    {
        var index = 0;
        var firstByte = startValue;
        byte secondByte = 0;
        for ( var i = 0; i < 256 * 256; i++ )
        {
            table[index++] = (ushort) ( firstByte + ( secondByte << 8 ) );
            firstByte++;
            if ( firstByte == 0 )
            {
                secondByte++;
            }
        }

        var array = table.ToArray();
        Array.Sort( array );
        array.AsSpan().CopyTo( table );
    }

    private static byte[] InternalDecompress( Span<byte> input, uint len )
    {
        Span<char> symbolTable = stackalloc char[256];
        Span<char> frequency = stackalloc char[256];
        Span<int> partialInput = stackalloc int[256 * 3];
        partialInput.Clear();

        for ( var i = 0; i < 256; i++ )
        {
            symbolTable[i] = (char) i;
        }

        input.Slice( 0, 1024 ).CopyTo( MemoryMarshal.AsBytes( partialInput ) );

        var sum = 0;
        for ( var i = 0; i < 256; i++ )
        {
            sum += partialInput[i];
        }

        if ( len == 0 )
        {
            len = (uint) sum;
        }

        if ( sum != len )
        {
            return [];
        }

        var output = new byte[len];
        var nonZeroCount = 0;
        for ( var i = 0; i < 256; i++ )
        {
            if ( partialInput[i] != 0 )
            {
                nonZeroCount++;
            }
        }

        Frequency( partialInput, frequency );

        for ( int i = 0, m = 0; i < nonZeroCount; ++i )
        {
            var freq = (byte) frequency[i];
            symbolTable[input[m + 1024]] = (char) freq;
            partialInput[freq + 256] = m + 1;
            m += partialInput[freq];
            partialInput[freq + 512] = m;
        }

        var val = (byte) symbolTable[0];
        var count = 0;

        if ( len == 0 )
        {
            return output;
        }

        do
        {
            ref var firstValRef = ref partialInput[val + 256];
            output[count] = val;

            if ( firstValRef >= partialInput[val + 512] )
            {
                if ( nonZeroCount-- > 0 )
                {
                    ShiftLeft( symbolTable, nonZeroCount );
                    val = (byte) symbolTable[0];
                }
            }
            else
            {
                var idx = (char) input[firstValRef + 1024];
                firstValRef++;

                if ( idx != 0 )
                {
                    ShiftLeft( symbolTable, idx );
                    symbolTable[(byte) idx] = (char) val;
                    val = (byte) symbolTable[0];
                }
            }

            count++;
        }
        while ( count < len );

        return output;
    }

    private static void Frequency( Span<int> input, Span<char> output )
    {
        Span<int> tmp = stackalloc int[256];
        input.Slice( 0, tmp.Length ).CopyTo( tmp );

        for ( var i = 0; i < 256; i++ )
        {
            uint value = 0;
            byte index = 0;

            for ( var j = 0; j < 256; j++ )
            {
                if ( tmp[j] > value )
                {
                    index = (byte) j;
                    value = (uint) tmp[j];
                }
            }

            if ( value == 0 )
            {
                break;
            }

            output[i] = (char) index;
            tmp[index] = 0;
        }
    }

    private static void ShiftLeft( Span<char> input, int max )
    {
        for ( var i = 0; i < max; ++i )
        {
            input[i] = input[i + 1];
        }
    }
}
